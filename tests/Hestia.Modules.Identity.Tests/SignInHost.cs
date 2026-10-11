using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.Identity.Infrastructure;
using Hestia.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Identity.Tests;

/// <summary>Helpers for the tests of the real sign-in (the host without <see cref="TestSignIn"/>).</summary>
internal static class SignInHost
{
    public const string Username = "alex";
    public const string Password = "correct horse battery";

    /// <summary>A host with the real sign-in and one account (<see cref="Username"/>, <see cref="Password"/>).</summary>
    public static async Task<HestiaApiFactory> WithAccountAsync(
        IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    {
        var factory = new HestiaApiFactory(settings, services, signedIn: false);
        _ = factory.CreateClient();
        await factory.WithScopeAsync(async provider =>
        {
            var users = provider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.True((await users.CreateAsync(new IdentityUser(Username), Password)).Succeeded);
            return 0;
        });
        return factory;
    }

    public static Task<HttpResponseMessage> LoginAsync(HttpClient client, string password = Password, string username = Username) =>
        client.PostAsJsonAsync("/api/Auth/login", new { username, password });

    /// <summary>Signs in on a new "device" and returns its session cookie ("finance_session=...").</summary>
    public static async Task<string> SignInElsewhereAsync(HestiaApiFactory factory, string password = Password)
    {
        var login = await LoginAsync(factory.CreateClient(), password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("finance_session=")).Split(';')[0];
    }

    /// <summary>The status that /api/Auth/me answers for a request with exactly this cookie.</summary>
    public static async Task<HttpStatusCode> WhoAmIAsync(HestiaApiFactory factory, string cookie)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Auth/me");
        request.Headers.Add("Cookie", cookie);
        return (await client.SendAsync(request)).StatusCode;
    }

    public static Task<int> SessionCountAsync(HestiaApiFactory factory) =>
        factory.WithScopeAsync(provider => provider.GetRequiredService<IdentityDbContext>().UserSessions.CountAsync());

    public static string? Detail(string problemJson) =>
        System.Text.Json.JsonDocument.Parse(problemJson).RootElement.GetProperty("detail").GetString();
}

/// <summary>The test server has no network peer. This plays the part of the client or proxy that connects.</summary>
internal sealed class FromAddress(IPAddress address) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = address;
            return nextMiddleware(context);
        });
        next(app);
    };
}
