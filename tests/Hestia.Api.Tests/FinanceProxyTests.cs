using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Hestia.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Api.Tests;

/// <summary>
/// The host sends the finance paths to the finance backend, and the client sees no change (T02).
/// Each test runs the real host and a <see cref="FakeFinance"/> on a real port.
/// </summary>
public class FinanceProxyTests
{
    /// <summary>The frontend's nginx container, on the Docker network "finance" (a trusted proxy).</summary>
    private const string Nginx = "172.18.0.5";

    /// <summary>The address of a phone on the tailnet, as nginx reports it in X-Forwarded-For.</summary>
    private const string Phone = "100.101.102.103";

    /// <summary>The name that clients use (Tailscale HTTPS). nginx passes it on in the Host header.</summary>
    private const string PublicHost = "hestia.tailnet.ts.net";

    [Fact]
    public async Task Request_ReachesFinance_Unchanged()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = Host(finance);
        var request = FromNginx(HttpMethod.Post, "/api/Transaction/import?accountId=7&note=a%20b");
        request.Content = new StringContent("{\"amount\":12.5}", Encoding.UTF8, "application/json");
        request.Headers.Add("Cookie", "finance_session=abc; theme=dark");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "read-only-token");

        var response = await Client(host).SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = Assert.Single(finance.Requests);
        Assert.Equal("POST", received.Method);
        Assert.Equal("/api/Transaction/import?accountId=7&note=a%20b", received.Target);
        Assert.Equal("{\"amount\":12.5}", received.Body);
        Assert.Equal("application/json; charset=utf-8", received.Header("Content-Type"));
        Assert.Equal("finance_session=abc; theme=dark", received.Header("Cookie"));
        Assert.Equal("Bearer read-only-token", received.Header("Authorization"));
        Assert.Equal(PublicHost, received.Header("Host"));
    }

    [Theory]
    [InlineData("https", true)]   // Tailscale HTTPS in front of nginx: the session cookie is Secure
    [InlineData(null, false)]     // plain HTTP all the way (local tests): no Secure flag
    public async Task ForwardedHeaders_GiveFinanceTheClientAndTheScheme(string? forwardedProto, bool https)
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = Host(finance);
        var request = FromNginx(HttpMethod.Post, "/api/Auth/login", forwardedProto);

        var response = await Client(host).SendAsync(request);

        var received = Assert.Single(finance.Requests);
        // What the finance rate limit (per client address) and the Secure cookie use.
        Assert.Equal(IPAddress.Parse(Phone), received.ClientAddress);
        Assert.Equal(https ? "https" : "http", received.Scheme);
        Assert.Equal(Phone, received.Header("X-Forwarded-For"));
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        Assert.StartsWith("finance_session=from-finance", cookie);
        Assert.Equal(https, cookie.Contains("; secure"));
    }

    [Fact]
    public async Task ForwardedHeaders_FromAnUntrustedClient_DoNotReachFinance()
    {
        await using var finance = await FakeFinance.StartAsync();
        // A client that talks to the host directly, not through nginx, and claims another address and HTTPS.
        await using var host = Host(finance, clientAddress: "203.0.113.7");
        var request = FromNginx(HttpMethod.Get, "/api/Auth/me");

        await Client(host).SendAsync(request);

        var received = Assert.Single(finance.Requests);
        Assert.Equal(IPAddress.Parse("203.0.113.7"), received.ClientAddress);
        Assert.Equal("http", received.Scheme);
        Assert.Equal("203.0.113.7", received.Header("X-Forwarded-For"));
    }

    [Fact]
    public async Task Response_ReachesClient_Unchanged()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = Host(finance);

        var response = await Client(host).SendAsync(FromNginx(HttpMethod.Get, "/api/Reports/summary"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("yes", Assert.Single(response.Headers.GetValues("X-Fake-Finance")));
        Assert.Equal("{\"source\":\"finance\"}", await response.Content.ReadAsStringAsync());
        Assert.Contains("httponly", Assert.Single(response.Headers.GetValues("Set-Cookie")).ToLowerInvariant());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    public async Task FinanceStatus_WithAnEmptyBody_StaysEmpty(int status)
    {
        // The host has status code pages for its own errors. It must not add a body to a finance response.
        await using var finance = await FakeFinance.StartAsync(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        await using var host = Host(finance);

        var response = await Client(host).SendAsync(FromNginx(HttpMethod.Get, "/api/Transaction/999"));

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Null(response.Content.Headers.ContentType);
    }

    /// <summary>Every finance controller path, with and without a sub-path, in any letter case.</summary>
    [Theory]
    [InlineData("/api/Accounts")]
    [InlineData("/api/Accounts/3")]
    [InlineData("/api/Auth/me")]
    [InlineData("/api/Budget")]
    [InlineData("/api/Categories/1")]
    [InlineData("/api/CategoryRules")]
    [InlineData("/api/FlowRules/12")]
    [InlineData("/api/Reports/monthly?year=2026&month=10")]
    [InlineData("/api/Tags")]
    [InlineData("/api/Transaction?categoryId=4&startDate=2026-10-01")]
    [InlineData("/api/transaction/5")]
    public async Task FinancePaths_GoToFinance(string target)
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = Host(finance);

        await Client(host).SendAsync(FromNginx(HttpMethod.Get, target));

        Assert.Equal(target, Assert.Single(finance.Requests).Target);
    }

    [Theory]
    [InlineData("/api/sample/items", HttpStatusCode.OK)]           // a module endpoint
    [InlineData("/api/no-such-module", HttpStatusCode.NotFound)]   // no module and no finance path
    [InlineData("/api/TransactionX", HttpStatusCode.NotFound)]     // a prefix matches whole path segments only
    [InlineData("/health/live", HttpStatusCode.OK)]
    public async Task OtherPaths_DoNotGoToFinance(string path, HttpStatusCode expected)
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = Host(finance);

        var response = await Client(host).SendAsync(FromNginx(HttpMethod.Get, path));

        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(finance.Requests);
    }

    [Fact]
    public async Task ModuleEndpoint_TakesPriority_OverAFinancePath()
    {
        await using var finance = await FakeFinance.StartAsync();
        // A finance path that is also a module prefix: the module wins.
        await using var host = Host(finance, more: new Dictionary<string, string?> { ["FinanceProxy:Paths:0"] = "/api/sample" });

        var response = await Client(host).SendAsync(FromNginx(HttpMethod.Get, "/api/sample/items"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(finance.Requests);
    }

    [Fact]
    public async Task FinanceDown_Is502()
    {
        // Nothing listens on port 9 (discard), so the connection is refused.
        await using var host = new HestiaApiFactory(new Dictionary<string, string?> { ["FinanceProxy:Address"] = "http://127.0.0.1:9" });

        var response = await Client(host).SendAsync(FromNginx(HttpMethod.Get, "/api/Accounts"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Ready_Is200_WhenFinanceAnswers()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = Host(finance);

        var response = await Client(host).GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_Is503_WhenFinanceIsNotReady()
    {
        await using var finance = await FakeFinance.StartAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        });
        await using var host = Host(finance);

        var response = await Client(host).GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotEmpty(finance.HealthChecks);
    }

    [Fact]
    public async Task Ready_Is503_WhenFinanceIsDown_AndLiveIs200()
    {
        await using var host = new HestiaApiFactory(new Dictionary<string, string?> { ["FinanceProxy:Address"] = "http://127.0.0.1:9" });
        var client = Client(host);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        // Liveness has no dependencies: a finance outage must not restart the host.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Theory]
    [InlineData("FinanceProxy:Address", "personal-finance-api:8080")]          // not absolute
    [InlineData("FinanceProxy:Address", "http://personal-finance-api:8080/api")] // has a path
    [InlineData("FinanceProxy:Paths:0", "/api/Accounts/")]                    // ends with '/'
    [InlineData("FinanceProxy:Paths:0", "api/Accounts")]                      // does not start with '/'
    public async Task Host_DoesNotStart_WithInvalidFinanceSettings(string key, string value)
    {
        await using var host = new HestiaApiFactory(new Dictionary<string, string?> { [key] = value });

        var error = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("FinanceProxy", error.ToString());
    }

    private static HestiaApiFactory Host(
        FakeFinance finance, string clientAddress = Nginx, IReadOnlyDictionary<string, string?>? more = null) =>
        new(finance.HostSettings(more), services =>
            services.AddSingleton<IStartupFilter>(new FromAddress(IPAddress.Parse(clientAddress))));

    /// <summary>No cookie container, so each test controls the Cookie header itself.</summary>
    private static HttpClient Client(HestiaApiFactory host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"http://{PublicHost}"),
            HandleCookies = false,
            AllowAutoRedirect = false
        });

    /// <summary>A request as nginx sends it: with the client address, and the scheme from Tailscale.</summary>
    private static HttpRequestMessage FromNginx(HttpMethod method, string target, string? forwardedProto = "https")
    {
        var request = new HttpRequestMessage(method, target);
        request.Headers.Add("X-Forwarded-For", Phone);
        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }
        return request;
    }

    /// <summary>The test server has no network peer; this plays the part of the connecting proxy or client.</summary>
    private sealed class FromAddress(IPAddress address) : IStartupFilter
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
}
