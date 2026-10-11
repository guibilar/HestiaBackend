using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Hestia.Api.Gateway;
using Hestia.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hestia.Api.Tests;

/// <summary>
/// T03: each request that the host sends to finance carries a short-lived gateway token (ES256), for the signed-in
/// user, with the scope full or read-only. Finance accepts only these tokens. The tests check each token as finance does
/// (<see cref="GatewayToken.Validate"/>: the standard JWT handler).
/// </summary>
public class GatewayTokenTests
{
    [Fact]
    public async Task ProxiedRequest_HasAToken_ThatFinanceAccepts_ForOneMinute()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = new HestiaApiFactory(finance.HostSettings());

        await host.CreateClient().GetAsync("/api/Accounts");

        var token = GatewayToken.Read(Assert.Single(finance.Requests));
        Assert.Equal(TestSignIn.UserId, token.Subject);
        Assert.Equal(TestSignIn.Username, token.Name);
        Assert.Equal("full", token.Scope);
        Assert.Equal(TimeSpan.FromSeconds(60), token.Lifetime);
        Assert.InRange(token.Expires, DateTimeOffset.UtcNow.AddSeconds(30), DateTimeOffset.UtcNow.AddSeconds(70));
    }

    [Fact]
    public async Task EachRequest_GetsItsOwnToken()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = new HestiaApiFactory(finance.HostSettings());
        var client = host.CreateClient();

        await client.GetAsync("/api/Accounts");
        await client.GetAsync("/api/Accounts");

        Assert.Equal(2, finance.Requests.Select(request => request.Header("Authorization")).Distinct().Count());
    }

    [Fact]
    public async Task ReadOnlyUser_GetsTheReadOnlyScope_AndOnlyTheFinanceQueries()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = new HestiaApiFactory(finance.HostSettings());
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestSignIn.ReadOnlyHeader, "yes");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/Budget/Filter", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/Accounts", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/Budget", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sample/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/sample/items", new { name = "x" })).StatusCode);

        Assert.Equal(["GET /api/Accounts", "POST /api/Budget/Filter"], finance.Requests.Select(r => $"{r.Method} {r.Target}"));
        Assert.All(finance.Requests, request => Assert.Equal("read-only", GatewayToken.Read(request).Scope));
    }

    [Fact]
    public async Task AnonymousRequest_DoesNotReachFinance()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = new HestiaApiFactory(finance.HostSettings(), signedIn: false);
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Accounts");
        // A cookie that the host does not know, and a token that finance would not accept.
        request.Headers.Add("Cookie", "finance_session=forged");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(finance.Requests);
    }

    [Theory]
    [InlineData(new[] { "finance_session=abc; theme=dark" }, "theme=dark")]
    [InlineData(new[] { "theme=dark;finance_session=abc;lang=fr" }, "theme=dark; lang=fr")]
    [InlineData(new[] { "finance_session=abc" }, "")]
    [InlineData(new[] { "a=1", "finance_session=abc; b=2" }, "a=1; b=2")]
    [InlineData(new[] { "my_finance_session=1" }, "my_finance_session=1")]
    public void SessionCookie_IsRemoved_OtherCookiesStay(string[] headers, string expected)
    {
        Assert.Equal(expected, FinanceProxy.WithoutSessionCookie(headers));
    }

    [Fact]
    public void Token_IsSignedWithTheKey_AndAnotherKeyDoesNotMatch()
    {
        using var tokens = new GatewayTokens(Options.Create(new FinanceProxyOptions { SigningKey = TestGatewayKey.SigningKey }), TimeProvider.System);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherTokens = new GatewayTokens(
            Options.Create(new FinanceProxyOptions { SigningKey = Convert.ToBase64String(otherKey.ExportPkcs8PrivateKey()) }), TimeProvider.System);
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "u1"),
             new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "alex")], "Test"));

        Assert.Equal("u1", GatewayToken.Validate(tokens.Create(user)).Subject);
        Assert.Throws<InvalidOperationException>(() => GatewayToken.Validate(otherTokens.Create(user)));
    }

    [Fact]
    public void Token_IsOnlyForASignedInUser()
    {
        using var tokens = new GatewayTokens(Options.Create(new FinanceProxyOptions { SigningKey = TestGatewayKey.SigningKey }), TimeProvider.System);

        Assert.Throws<InvalidOperationException>(() => tokens.Create(new System.Security.Claims.ClaimsPrincipal()));
    }

    [Theory]
    [InlineData("")]                                   // not set
    [InlineData("not a key")]                          // not base64 or PEM
    [InlineData("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOcgvHe/bxO/w9Er4AaQy/EKwp7ePj5Kx/NbXCDl+Ej+EGc4sU1HpckO7etOcvzGoAhvh3/j6oN7d5MJIrKnDgg==")] // a public key
    public async Task Host_DoesNotStart_WithoutAValidSigningKey(string key)
    {
        await using var host = new HestiaApiFactory(new Dictionary<string, string?> { ["FinanceProxy:SigningKey"] = key });

        var error = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("FinanceProxy:SigningKey", error.ToString());
    }

    [Theory]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("-----BEGIN EC PRIVATE KEY-----")]
    [InlineData("pkcs8")]
    [InlineData("sec1")]
    public void SigningKey_CanBePemOrBase64(string form)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var value = form switch
        {
            "-----BEGIN PRIVATE KEY-----" => key.ExportPkcs8PrivateKeyPem(),
            "-----BEGIN EC PRIVATE KEY-----" => key.ExportECPrivateKeyPem(),
            "pkcs8" => Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
            _ => Convert.ToBase64String(key.ExportECPrivateKey())
        };

        Assert.Equal(key.ExportSubjectPublicKeyInfoPem(), GatewaySigningKey.PublicKeyPem(value));
    }

    [Fact]
    public async Task NoModuleEndpoint_IsOnAFinancePath()
    {
        await using var host = new HestiaApiFactory();
        _ = host.CreateClient();
        var options = host.Services.GetRequiredService<IOptions<FinanceProxyOptions>>().Value;
        var dataSources = host.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>();

        Assert.Empty(FinanceProxy.Overlaps(dataSources, options.Paths));
    }

    [Fact]
    public async Task AModuleEndpointOnAFinancePath_IsFound_ForTheStartWarning()
    {
        await using var host = new HestiaApiFactory(new Dictionary<string, string?> { ["FinanceProxy:Paths:0"] = "/api/Sample" });
        _ = host.CreateClient();
        var options = host.Services.GetRequiredService<IOptions<FinanceProxyOptions>>().Value;
        var dataSources = host.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>();

        Assert.Equal(["/api/sample/activity", "/api/sample/items"], FinanceProxy.Overlaps(dataSources, options.Paths));
    }
}
