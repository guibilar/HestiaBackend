using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Hestia.Modules.Identity.Contracts;
using Hestia.Testing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Identity.Tests;

/// <summary>
/// Identity:ReadOnlyToken: a bearer token that reads everything and changes nothing (moved from finance in T03).
/// The host refuses writes. For finance, it sends the read-only scope in the gateway token.
/// </summary>
public class ReadOnlyTokenTests
{
    private const string Token = "a-read-only-token-long-enough-for-the-check";

    private static HestiaApiFactory WithToken(FakeFinance finance, string? token = Token) =>
        new(finance.HostSettings(new Dictionary<string, string?> { ["Identity:ReadOnlyToken"] = token }), signedIn: false);

    private static HttpClient ClientWith(HestiaApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Token_ReadsModulesAndFinance()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = WithToken(finance);
        var client = ClientWith(factory, Token);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sample/items")).StatusCode);
        Assert.Equal("read-only token", (await (await client.GetAsync("/api/Auth/me")).ReadAsync<CurrentUserDto>()).Username);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/Transaction/Filter", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/Reports/breakdown", new { interval = "Month" })).StatusCode);

        // Finance gets a gateway token with the read-only scope, never the token itself.
        Assert.Equal(3, finance.Requests.Count);
        Assert.All(finance.Requests, request =>
        {
            Assert.DoesNotContain(Token, request.Header("Authorization"));
            Assert.Equal("read-only", GatewayToken.Read(request).Scope);
        });
    }

    [Fact]
    public async Task Token_ChangesNothing()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = WithToken(finance);
        var client = ClientWith(factory, Token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/sample/items", new { name = "Sneaky" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/Auth/change-password", new { currentPassword = "x", newPassword = "y" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/Accounts", new { name = "Sneaky", type = "Chequing" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/Transaction/5")).StatusCode);
        // The host refused them: finance got nothing.
        Assert.Empty(finance.Requests);
    }

    [Fact]
    public async Task Token_IsRefusedByEveryEndpointThatChangesData()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = WithToken(finance);
        var client = ClientWith(factory, Token);
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .ToList();

        var queries = endpoints
            .Where(e => e.Metadata.GetMetadata<Hestia.Platform.Auth.ReadOnlyQueryAttribute>() is not null)
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["/api/Budget/Filter", "/api/CategoryRules/Preview", "/api/Reports/breakdown", "/api/Reports/merchants",
            "/api/Transaction/Filter"], queries);

        foreach (var endpoint in endpoints)
        {
            var path = "/" + string.Join('/', endpoint.RoutePattern.PathSegments.Select(segment =>
                segment.IsSimple && segment.Parts[0] is RoutePatternLiteralPart literal ? literal.Content : Guid.NewGuid().ToString()));
            var readOnly = endpoint.Metadata.GetMetadata<Hestia.Platform.Auth.ReadOnlyQueryAttribute>() is not null;
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET", "POST", "PUT", "DELETE"])
            {
                var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) });
                if (HttpMethods.IsGet(method) || readOnly)
                {
                    Assert.True(response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
                        $"{method} {path} answered {response.StatusCode}");
                }
                else
                {
                    Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} answered {response.StatusCode}");
                }
            }
        }
    }

    [Theory]
    [InlineData(Token + "x")]
    [InlineData("")]
    public async Task WrongToken_IsUnauthorized(string token)
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = WithToken(finance);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ClientWith(factory, token).GetAsync("/api/sample/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ClientWith(factory, token).GetAsync("/api/Accounts")).StatusCode);
        Assert.Empty(finance.Requests);
    }

    [Fact]
    public async Task WithoutAConfiguredToken_NoBearerTokenWorks()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = WithToken(finance, null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ClientWith(factory, Token).GetAsync("/api/sample/items")).StatusCode);
    }

    [Fact]
    public async Task AShortToken_StopsTheStart()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = WithToken(finance, "too-short");

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("at least 32 characters", error.ToString());
    }
}
