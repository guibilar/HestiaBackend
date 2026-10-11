using System.Net;
using Hestia.Platform.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace Hestia.Platform.Tests;

/// <summary>X-Forwarded-Proto and X-Forwarded-For count only when a trusted proxy sends them.</summary>
public class ReverseProxyTests
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:TrustedNetworks:0"] = "172.16.0.0/12"
        })
        .Build();

    [Theory]
    [InlineData("172.18.0.5", "https 203.0.113.9")]     // nginx container on a Docker network
    [InlineData("127.0.0.1", "https 203.0.113.9")]      // a proxy on the same machine
    [InlineData("203.0.113.7", "http 203.0.113.7")]     // not a trusted proxy: its headers are ignored
    public async Task ForwardedHeaders_CountOnlyFromTrustedProxies(string proxyAddress, string expected)
    {
        using var host = await WebTestServer.StartAsync(
            services => services.AddReverseProxySupport(Configuration),
            app =>
            {
                // The test server has no network peer; this plays the part of the connecting proxy.
                app.Use((context, next) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(proxyAddress);
                    return next(context);
                });
                app.UseForwardedHeaders();
                app.Run(context => context.Response.WriteAsync(
                    $"{context.Request.Scheme} {context.Connection.RemoteIpAddress}"));
            });
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.9");

        var response = await host.GetTestClient().SendAsync(request);

        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }
}
