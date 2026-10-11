using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Platform.Hosting;

/// <summary>
/// The host runs behind the frontend's nginx (and a TLS proxy in front of it). Their X-Forwarded-Proto and
/// X-Forwarded-For headers tell how the client really connected. The host believes them only from proxies on the
/// networks in ReverseProxy:TrustedNetworks, so a client that talks to the host directly cannot fake them.
/// </summary>
public static class ReverseProxy
{
    public const string SectionName = "ReverseProxy";

    public static IServiceCollection AddReverseProxySupport(this IServiceCollection services, IConfiguration configuration)
    {
        var networks = configuration.GetSection($"{SectionName}:TrustedNetworks").Get<string[]>() ?? [];
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Loopback is trusted by default. Add the configured networks (for example Docker's private ranges).
            foreach (var network in networks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });
        return services;
    }
}
