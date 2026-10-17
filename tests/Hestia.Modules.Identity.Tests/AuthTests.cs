using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Hestia.Modules.Identity.Application;
using Hestia.Modules.Identity.Contracts;
using Hestia.Modules.Identity.Infrastructure;
using Hestia.Testing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Hestia.Modules.Identity.Tests.SignInHost;

namespace Hestia.Modules.Identity.Tests;

/// <summary>
/// The real cookie sign-in, moved from the finance backend in T03 (the same tests as finance AuthTests).
/// The clients see the same endpoints, cookie, and answers as before.
/// </summary>
public class AuthTests
{
    [Fact]
    public async Task OnlySignInAndHealthChecks_AreOpenToAnonymousUsers()
    {
        await using var factory = new HestiaApiFactory(signedIn: false);
        var client = factory.CreateClient();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        var anonymous = endpoints
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["/api/Auth/login", "/api/Auth/logout", "/health/live", "/health/ready"], anonymous);

        // The other endpoints (modules and the finance proxy) answer 401 without a session. Authorization runs before
        // the route values or the body are checked, and before the proxy sends anything to finance.
        var others = endpoints.Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null).ToList();
        Assert.Contains(others, e => e.RoutePattern.RawText!.StartsWith("/api/Accounts", StringComparison.Ordinal));
        foreach (var endpoint in others)
        {
            var path = "/" + string.Join('/', endpoint.RoutePattern.PathSegments.Select(segment =>
                segment.IsSimple && segment.Parts[0] is RoutePatternLiteralPart literal ? literal.Content : Guid.NewGuid().ToString()));
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET", "POST"])
            {
                var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
                {
                    Content = JsonContent.Create(new { })
                });
                Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} answered {response.StatusCode}");
            }
        }
    }

    [Fact]
    public async Task Me_AsJson_HasTheUsernameAndTheScope()
    {
        await using var factory = await WithAccountAsync();
        var client = factory.CreateClient();
        await LoginAsync(client);

        // The clients read these two names (T60).
        using var me = JsonDocument.Parse(await client.GetStringAsync("/api/Auth/me"));
        Assert.Equal(Username, me.RootElement.GetProperty("username").GetString());
        Assert.Equal("full", me.RootElement.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task TheOpenApiDocument_DescribesTheScopeOfTheCurrentUser()
    {
        await using var factory = new HestiaApiFactory();

        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var user = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("CurrentUserDto");
        Assert.True(user.GetProperty("properties").TryGetProperty("scope", out _));
        var me = document.RootElement.GetProperty("paths").GetProperty("/api/Auth/me").GetProperty("get");
        Assert.Contains("CurrentUserDto",
            me.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
    }

    [Fact]
    public async Task Login_SetsAStrictHttpOnlyCookie_ThatOpensTheModulesUntilLogout()
    {
        await using var factory = await WithAccountAsync();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/Auth/me")).StatusCode);
        var login = await LoginAsync(client, username: "ALEX");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(new CurrentUserDto(Username, "full"), await login.ReadAsync<CurrentUserDto>());
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("finance_session="));
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", cookie.ToLowerInvariant());
        Assert.DoesNotContain("expires=", cookie.ToLowerInvariant()); // not "remember me": a session cookie

        // A cookie user can write: the scope is always "full" (T60).
        Assert.Equal(new CurrentUserDto(Username, "full"), await (await client.GetAsync("/api/Auth/me")).ReadAsync<CurrentUserDto>());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sample/items")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/Auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/Auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/sample/items")).StatusCode);
    }

    [Fact]
    public async Task SignedIn_FinanceRequests_ReachFinance_WithAGatewayToken_AndWithoutTheSessionCookie()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = await WithAccountAsync(finance.HostSettings());
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/Accounts")).StatusCode);
        Assert.Empty(finance.Requests);

        await LoginAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Accounts")).StatusCode);

        var received = Assert.Single(finance.Requests);
        Assert.StartsWith("Bearer ey", received.Header("Authorization"));
        Assert.Null(received.Header("Cookie"));
    }

    [Fact]
    public async Task RememberMe_MakesThePersistentCookie()
    {
        await using var factory = await WithAccountAsync();

        var login = await factory.CreateClient().PostAsJsonAsync("/api/Auth/login",
            new { username = Username, password = Password, rememberMe = true });

        Assert.Contains("expires=", login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("finance_session=")).ToLowerInvariant());
    }

    [Fact]
    public async Task Login_WithoutAUsernameOrPassword_IsABadRequest()
    {
        await using var factory = await WithAccountAsync();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/Auth/login", new { username = Username, password = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Password", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WrongPasswordAndUnknownUser_GetTheSameAnswer()
    {
        await using var factory = await WithAccountAsync();
        var client = factory.CreateClient();

        var wrongPassword = await LoginAsync(client, "not the password");
        var unknownUser = await LoginAsync(client, username: "nobody");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(Detail(await wrongPassword.Content.ReadAsStringAsync()), Detail(await unknownUser.Content.ReadAsStringAsync()));
        Assert.False(wrongPassword.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task FiveWrongPasswords_LockTheAccount_EvenForTheRightPassword()
    {
        await using var factory = await WithAccountAsync();
        var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            await LoginAsync(client, "not the password");
        }
        var locked = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/Auth/me")).StatusCode);
    }

    [Fact]
    public async Task AnUnknownUsername_IsLockedOutAfterAsManyAttempts_AsAnAccount()
    {
        await using var factory = await WithAccountAsync();
        var client = factory.CreateClient();

        var account = new List<HttpResponseMessage>();
        var unknown = new List<HttpResponseMessage>();
        for (var i = 0; i < 5; i++)
        {
            account.Add(await LoginAsync(client, "not the password"));
            unknown.Add(await LoginAsync(client, "not the password", username: "nobody"));
        }

        // Four refusals, then the lockout, for both: a guess cannot tell which name has an account.
        Assert.Equal(account.Select(r => r.StatusCode), unknown.Select(r => r.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, unknown[^1].StatusCode);
        Assert.Equal(Detail(await account[^1].Content.ReadAsStringAsync()), Detail(await unknown[^1].Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task WrongCurrentPasswords_WhenChangingIt_LockTheAccountToo()
    {
        await using var factory = await WithAccountAsync();
        var client = factory.CreateClient();
        await LoginAsync(client);

        HttpResponseMessage last = null!;
        for (var i = 0; i < 5; i++)
        {
            last = await client.PostAsJsonAsync("/api/Auth/change-password", new { currentPassword = "a wrong guess", newPassword = "a much longer passphrase" });
        }

        Assert.Equal(HttpStatusCode.BadRequest, last.StatusCode);
        Assert.Contains("Too many wrong passwords", await last.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(factory.CreateClient())).StatusCode);
    }

    [Fact]
    public async Task PasswordAttempts_AreLimitedPerAddress_RightOrWrong()
    {
        await using var factory = await WithAccountAsync();
        var client = factory.CreateClient();

        // Different names that do not exist, so no lockout occurs.
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "guess", username: $"user{i}")).StatusCode);
        }
        var limited = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Contains("from this device", Detail(await limited.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task PasswordAttempts_AreLimitedPerClientAddress_ThatTheProxyReports()
    {
        // nginx (a trusted proxy) reports the client address in X-Forwarded-For. Each client has its own limit.
        await using var factory = await WithAccountAsync(services: services =>
            services.AddSingleton<IStartupFilter>(new FromAddress(IPAddress.Parse("172.18.0.5"))));
        var client = factory.CreateClient();

        for (var i = 0; i < 10; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Auth/login")
            {
                Content = JsonContent.Create(new { username = $"user{i}", password = "guess" })
            };
            request.Headers.Add("X-Forwarded-For", "100.64.0.1");
            await client.SendAsync(request);
        }
        var otherClient = new HttpRequestMessage(HttpMethod.Post, "/api/Auth/login")
        {
            Content = JsonContent.Create(new { username = Username, password = Password })
        };
        otherClient.Headers.Add("X-Forwarded-For", "100.64.0.2");

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(otherClient)).StatusCode);
    }

    [Theory]
    [InlineData("172.18.0.5", "https", true)]    // nginx on a Docker network, TLS in front
    [InlineData("172.18.0.5", null, false)]      // plain HTTP all the way
    [InlineData("203.0.113.7", "https", false)]  // not a trusted proxy: its header is ignored
    public async Task SessionCookie_IsSecure_OnlyWhenATrustedProxySaysHttps(string clientAddress, string? forwardedProto, bool secure)
    {
        await using var factory = await WithAccountAsync(services: services =>
            services.AddSingleton<IStartupFilter>(new FromAddress(IPAddress.Parse(clientAddress))));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Auth/login")
        {
            Content = JsonContent.Create(new { username = Username, password = Password })
        };
        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("finance_session=")).ToLowerInvariant();
        Assert.Equal(secure, cookie.Contains("; secure"));
    }

    [Fact]
    public async Task ChangePassword_ChecksTheCurrentOne_AndTheNewOneIsUsedFromThenOn()
    {
        await using var factory = await WithAccountAsync();
        var client = factory.CreateClient();
        await LoginAsync(client);
        const string newPassword = "a much longer passphrase";

        var wrongCurrent = await client.PostAsJsonAsync("/api/Auth/change-password", new { currentPassword = "nope", newPassword });
        var tooShort = await client.PostAsJsonAsync("/api/Auth/change-password", new { currentPassword = Password, newPassword = "short" });
        var changed = await client.PostAsJsonAsync("/api/Auth/change-password", new { currentPassword = Password, newPassword });

        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);
        Assert.Contains("currentPassword", await wrongCurrent.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Contains("newPassword", await tooShort.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Auth/me")).StatusCode); // still signed in

        var other = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(other)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(other, newPassword)).StatusCode);
    }

    // ---- The sessions are on the server, so they can end

    [Fact]
    public async Task Logout_EndsTheSessionOnTheServer_SoACopyOfTheCookieStopsWorking()
    {
        await using var factory = await WithAccountAsync();
        var cookie = await SignInElsewhereAsync(factory);
        var copy = cookie;
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(factory, copy));

        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        var logout = new HttpRequestMessage(HttpMethod.Post, "/api/Auth/logout");
        logout.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(logout)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(factory, copy));
        Assert.Equal(0, await SessionCountAsync(factory));
    }

    [Fact]
    public async Task TheCookieCarriesOnlyASessionKey_TheSignInIsStoredOnTheServer()
    {
        await using var factory = await WithAccountAsync();

        await SignInElsewhereAsync(factory);
        await SignInElsewhereAsync(factory);

        Assert.Equal(2, await SessionCountAsync(factory));
    }

    [Fact]
    public async Task ChangePassword_SignsOutEveryOtherSessionAtOnce_AndKeepsThisOne()
    {
        await using var factory = await WithAccountAsync();
        var laptop = factory.CreateClient();
        await LoginAsync(laptop);
        var phone = await SignInElsewhereAsync(factory);
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(factory, phone));

        var changed = await laptop.PostAsJsonAsync("/api/Auth/change-password",
            new { currentPassword = Password, newPassword = "a much longer passphrase" });

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(factory, phone));
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/Auth/me")).StatusCode);
        Assert.Equal(1, await SessionCountAsync(factory));
    }

    [Fact]
    public async Task SignOutOthers_EndsTheOtherSessions_AndSaysHowMany()
    {
        await using var factory = await WithAccountAsync();
        var laptop = factory.CreateClient();
        await LoginAsync(laptop);
        var phone = await SignInElsewhereAsync(factory);
        var tablet = await SignInElsewhereAsync(factory);

        var first = await (await laptop.PostAsync("/api/Auth/sign-out-others", null)).ReadAsync<SignedOutSessionsDto>();
        var again = await (await laptop.PostAsync("/api/Auth/sign-out-others", null)).ReadAsync<SignedOutSessionsDto>();

        Assert.Equal((2, 0), (first.Count, again.Count));
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(factory, phone));
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(factory, tablet));
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/Auth/me")).StatusCode);
    }

    [Fact]
    public async Task SignOutOthers_KeepsThisSession_AfterIdentityRefreshedTheSignedInUser()
    {
        // The security stamp is checked on every request here (every 30 minutes in use): each check makes a new
        // signed-in user.
        await using var factory = await WithAccountAsync(services: services =>
            services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero));
        var laptop = factory.CreateClient();
        await LoginAsync(laptop);
        var phone = await SignInElsewhereAsync(factory);

        var signedOut = await (await laptop.PostAsync("/api/Auth/sign-out-others", null)).ReadAsync<SignedOutSessionsDto>();

        Assert.Equal(1, signedOut.Count);
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(factory, phone));
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/Auth/me")).StatusCode);
    }

    [Fact]
    public async Task Login_WithTheCookieOfAnotherSession_StartsANewSession_AndEndsThatOne()
    {
        // Session fixation: the cookie handler renewed the session of the request, so the new sign-in kept its key and a
        // copy of the earlier cookie was then signed in as the new user.
        await using var factory = await WithAccountAsync();
        await factory.WithScopeAsync(async provider =>
        {
            var users = provider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.True((await users.CreateAsync(new IdentityUser("sam"), "another long passphrase")).Succeeded);
            return 0;
        });
        var alex = await SignInElsewhereAsync(factory);
        var alexSession = Assert.Single(await SessionsAsync(factory));

        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = new HttpRequestMessage(HttpMethod.Post, "/api/Auth/login")
        {
            Content = JsonContent.Create(new { username = "sam", password = "another long passphrase" })
        };
        login.Headers.Add("Cookie", alex);
        var signedIn = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        var sam = signedIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("finance_session=")).Split(';')[0];

        // A new session (a new key in the cookie), and the session of alex ended: its cookie signs nobody in.
        var samSession = Assert.Single(await SessionsAsync(factory));
        Assert.NotEqual(alexSession.Id, samSession.Id);
        var samId = await factory.WithScopeAsync(async provider =>
            (await provider.GetRequiredService<UserManager<IdentityUser>>().FindByNameAsync("sam"))!.Id);
        Assert.Equal(samId, samSession.UserId);
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(factory, alex));
        var me = new HttpRequestMessage(HttpMethod.Get, "/api/Auth/me");
        me.Headers.Add("Cookie", sam);
        Assert.Equal("sam", (await (await client.SendAsync(me)).ReadAsync<CurrentUserDto>()).Username);
    }

    [Fact]
    public async Task Login_Again_WithTheSameAccount_ReplacesTheSession_AndAFailedLoginKeepsIt()
    {
        await using var factory = await WithAccountAsync();
        var first = await SignInElsewhereAsync(factory);
        var firstSession = Assert.Single(await SessionsAsync(factory));
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });

        var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/Auth/login") { Content = JsonContent.Create(new { username = Username, password = "wrong" }) };
        wrong.Headers.Add("Cookie", first);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(factory, first));

        var again = new HttpRequestMessage(HttpMethod.Post, "/api/Auth/login") { Content = JsonContent.Create(new { username = Username, password = Password }) };
        again.Headers.Add("Cookie", first);
        var signedIn = await client.SendAsync(again);
        var second = signedIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("finance_session=")).Split(';')[0];

        Assert.NotEqual(firstSession.Id, Assert.Single(await SessionsAsync(factory)).Id);
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(factory, first));
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(factory, second));
    }

    [Fact]
    public async Task ASession_NeverChangesHands_ARenewalForAnotherUserEndsIt()
    {
        await using var factory = await WithAccountAsync();
        var cookie = await SignInElsewhereAsync(factory);
        var session = Assert.Single(await SessionsAsync(factory));
        var someoneElse = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "someone-else")], "test"));

        await factory.Services.GetRequiredService<DbTicketStore>().RenewAsync(
            session.Id, new AuthenticationTicket(someoneElse, IdentityConstants.ApplicationScheme));

        Assert.Empty(await SessionsAsync(factory));
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(factory, cookie));
    }

    private static Task<List<(string Id, string UserId)>> SessionsAsync(HestiaApiFactory factory) =>
        factory.WithScopeAsync(async provider =>
            (await provider.GetRequiredService<IdentityDbContext>().UserSessions.ToListAsync())
                .Select(session => (session.Id, session.UserId)).ToList());

    // ---- The account from the settings

    [Fact]
    public async Task InitialUser_IsMadeAtStart_AndCanBeReset()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Identity:InitialUser:Username"] = Username,
            ["Identity:InitialUser:Password"] = Password,
        };
        await using var factory = new HestiaApiFactory(settings, signedIn: false);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(factory.CreateClient())).StatusCode);

        // A second start with ResetPassword sets the password from the settings again (for example, after it was forgotten).
        await factory.WithScopeAsync(async provider =>
        {
            var users = provider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByNameAsync(Username))!;
            await users.ChangePasswordAsync(user, Password, "forgotten password here");
            await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1));
            return 0;
        });
        var restarted = factory.WithWebHostBuilder(b => b.UseSetting("Identity:InitialUser:ResetPassword", "true"));

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(restarted.CreateClient())).StatusCode);
    }

    [Fact]
    public async Task InitialUser_WithAWeakPassword_StopsTheStart()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Identity:InitialUser:Username"] = Username,
            ["Identity:InitialUser:Password"] = "short",
        };
        await using var factory = new HestiaApiFactory(settings, signedIn: false);

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("at least 12 characters", error.ToString());
    }
}
