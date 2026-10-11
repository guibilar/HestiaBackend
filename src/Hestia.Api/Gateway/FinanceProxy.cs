using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Hestia.Api.Gateway;

/// <summary>
/// The host is the gateway in front of the finance backend (D6). Requests on the finance paths go to finance
/// unchanged: the method, the path, the query string, the body, the cookies, the Authorization header, and the
/// Host header that nginx sent. The response comes back unchanged too.
///
/// The host sets X-Forwarded-For and X-Forwarded-Proto to the client address and the scheme that it found
/// itself (after <c>UseForwardedHeaders</c>). So finance sees the same client and scheme as before, when nginx
/// called it directly: the rate limit per address and the Secure session cookie still work. A client cannot
/// fake these headers: the host believes them only from trusted proxies, and it replaces them for finance.
/// </summary>
public static class FinanceProxy
{
    /// <summary>
    /// Endpoint order of the finance routes. Module endpoints have order 0, so a module endpoint on the same
    /// path always takes priority over the proxy.
    /// </summary>
    public const int RouteOrder = 1000;

    public const string ClusterId = "finance";

    public static IServiceCollection AddFinanceProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FinanceProxyOptions>()
            .Bind(configuration.GetSection(FinanceProxyOptions.SectionName))
            .Validate(options => !options.Validate().Any(),
                $"The {FinanceProxyOptions.SectionName} settings are not valid. See FinanceProxyOptions.")
            .ValidateOnStart();

        services.AddReverseProxy();
        // Read from the options when the proxy starts, so test settings and environment variables apply.
        services.AddSingleton<IProxyConfigProvider>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<FinanceProxyOptions>>().Value;
            return new InMemoryConfigProvider(Routes(options), Clusters(options));
        });

        services.AddHttpClient(FinanceHealthCheck.HttpClientName, (provider, client) =>
            client.Timeout = provider.GetRequiredService<IOptions<FinanceProxyOptions>>().Value.HealthTimeout);
        return services;
    }

    public static IEndpointRouteBuilder MapFinanceProxy(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapReverseProxy(proxy =>
        {
            // Finance writes its own error bodies. The host must not add a body to a finance response.
            proxy.Use((context, next) =>
            {
                if (context.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
                {
                    statusCodePages.Enabled = false;
                }
                return next(context);
            });
            // The standard YARP steps (a custom pipeline replaces them, so add them again).
            proxy.UseSessionAffinity();
            proxy.UseLoadBalancing();
            proxy.UsePassiveHealthChecks();
        });
        return endpoints;
    }

    internal static IReadOnlyList<RouteConfig> Routes(FinanceProxyOptions options) =>
        options.Paths
            .Select((path, index) => new RouteConfig
            {
                RouteId = $"finance-{index}",
                ClusterId = ClusterId,
                Order = RouteOrder,
                // A catch-all can be empty, so "/api/Transaction" and "/api/Transaction/5" both match.
                Match = new RouteMatch { Path = $"{path}/{{**rest}}" },
                Transforms =
                [
                    // Keep the Host header from nginx: finance builds absolute URLs (for example Location) with it.
                    new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" }
                ]
            })
            .ToList();

    internal static IReadOnlyList<ClusterConfig> Clusters(FinanceProxyOptions options) =>
    [
        new ClusterConfig
        {
            ClusterId = ClusterId,
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["finance-api"] = new() { Address = options.Address!.ToString() }
            },
            HttpRequest = new ForwarderRequestConfig { ActivityTimeout = options.ActivityTimeout }
        }
    ];
}
