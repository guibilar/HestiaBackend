using System.Collections.Concurrent;
using System.Net;
using Hestia.Platform.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hestia.Testing;

/// <summary>
/// A stand-in for the finance backend, on a real port of 127.0.0.1 (the host's proxy sends real HTTP requests).
/// It records each request. Like finance, it believes X-Forwarded-For and X-Forwarded-Proto only from trusted
/// proxies (loopback here), so <see cref="FinanceRequest.ClientAddress"/> and <see cref="FinanceRequest.Scheme"/>
/// show what the real finance backend would see.
/// </summary>
public sealed class FakeFinance : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly ConcurrentQueue<FinanceRequest> _requests = new();

    private FakeFinance(Func<HttpContext, Task> respond)
    {
        Respond = respond;
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel()
                .UseUrls("http://127.0.0.1:0")
                .ConfigureServices(services => services.AddReverseProxySupport(new ConfigurationBuilder().Build()))
                .Configure(app =>
                {
                    // The raw headers, before UseForwardedHeaders reads and removes the X-Forwarded ones.
                    app.Use(async (context, next) =>
                    {
                        context.Items[RawHeadersKey] = context.Request.Headers.ToDictionary(
                            header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                        await next(context);
                    });
                    app.UseForwardedHeaders();
                    app.Run(HandleAsync);
                }))
            .Build();
    }

    private const string RawHeadersKey = "raw-headers";

    /// <summary>The answer to each request. The default: 200 on the health path, else <see cref="DefaultResponseAsync"/>.</summary>
    public Func<HttpContext, Task> Respond { get; set; }

    /// <summary>The address to give the host in <c>FinanceProxy:Address</c>.</summary>
    public Uri Address { get; private set; } = null!;

    /// <summary>The requests that reached the fake, except the health checks.</summary>
    public IReadOnlyList<FinanceRequest> Requests => _requests.Where(request => !request.IsHealthCheck).ToList();

    /// <summary>The readiness checks that reached the fake (the host's /health/ready calls them).</summary>
    public IReadOnlyList<FinanceRequest> HealthChecks => _requests.Where(request => request.IsHealthCheck).ToList();

    public static async Task<FakeFinance> StartAsync(Func<HttpContext, Task>? respond = null)
    {
        var fake = new FakeFinance(respond ?? DefaultResponseAsync);
        await fake._host.StartAsync();
        var address = fake._host.Services.GetRequiredService<IServer>().Features
            .GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        fake.Address = new Uri(address);
        return fake;
    }

    /// <summary>Settings for <see cref="HestiaApiFactory"/> that send the finance paths to this fake.</summary>
    public Dictionary<string, string?> HostSettings(IReadOnlyDictionary<string, string?>? more = null)
    {
        var settings = new Dictionary<string, string?> { ["FinanceProxy:Address"] = Address.ToString() };
        foreach (var (key, value) in more ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }
        return settings;
    }

    /// <summary>
    /// 200 on /health/ready. Else 200 with a JSON body, a session cookie (Secure only over HTTPS, as in finance),
    /// and the header <c>X-Fake-Finance: yes</c>.
    /// </summary>
    public static async Task DefaultResponseAsync(HttpContext context)
    {
        if (context.Request.Path == "/health/ready")
        {
            await context.Response.WriteAsync("Healthy");
            return;
        }
        context.Response.Headers["X-Fake-Finance"] = "yes";
        context.Response.Cookies.Append("finance_session", "from-finance", new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/"
        });
        await context.Response.WriteAsJsonAsync(new { source = "finance" });
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        _requests.Enqueue(new FinanceRequest(
            context.Request.Method,
            context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget,
            (Dictionary<string, string>)context.Items[RawHeadersKey]!,
            body,
            context.Request.Scheme,
            context.Connection.RemoteIpAddress,
            context.Request.Path == "/health/ready"));
        await Respond(context);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

/// <param name="Target">The path and query string exactly as they arrived.</param>
/// <param name="Headers">The request headers as they arrived (case-insensitive names).</param>
/// <param name="Scheme">The scheme after UseForwardedHeaders: what finance uses for the Secure cookie.</param>
/// <param name="ClientAddress">The client address after UseForwardedHeaders: what the finance rate limit uses.</param>
public sealed record FinanceRequest(
    string Method,
    string Target,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    string Scheme,
    IPAddress? ClientAddress,
    bool IsHealthCheck)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}
