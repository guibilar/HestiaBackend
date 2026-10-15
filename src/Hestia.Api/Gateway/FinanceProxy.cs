using System.Net.Http.Headers;
using Hestia.Platform.Auth;
using Hestia.Platform.Finance;
using Hestia.Platform.Modules;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Transforms;

namespace Hestia.Api.Gateway;

/// <summary>
/// The host is the gateway in front of the finance backend (D6). Requests on the finance paths go to finance
/// unchanged: the method, the path, the query string, the body, the cookies, the Authorization header, and the
/// Host header that nginx sent. The response comes back unchanged too.
///
/// Sign-in (T03): only a signed-in user reaches finance (the fallback policy of the Identity module covers the proxy
/// routes too). The host replaces the Authorization header with a short-lived gateway token for the user
/// (<see cref="GatewayTokens"/>), and removes its own session cookie from the request. Finance does not need it.
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

    /// <summary>The route metadata key of a finance read-only query (see <see cref="FinanceProxyOptions.ReadOnlyQueries"/>).</summary>
    private const string ReadOnlyQueryKey = "ReadOnlyQuery";

    public static IServiceCollection AddFinanceProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FinanceProxyOptions>()
            .Bind(configuration.GetSection(FinanceProxyOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FinanceProxyOptions>, FinanceProxyOptionsValidator>();

        services.AddSingleton<GatewayTokens>();
        services.AddSingleton<IModuleCommand, GatewayKeyCommand>();
        services.AddHttpContextAccessor();
        // The shared finance client for modules (D13).
        services.AddHttpClient<IFinanceClient, FinanceClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<FinanceProxyOptions>>().Value;
            client.BaseAddress = options.Address;
            client.Timeout = options.ClientTimeout;
        });

        services.AddReverseProxy()
            .AddTransforms(context =>
            {
                if (context.Route.ClusterId == ClusterId)
                {
                    context.AddRequestTransform(AddGatewayToken);
                }
            });
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
        })
        // A finance read-only query is open to the read-only user, as a module endpoint with ReadOnlyQuery is.
        .ConfigureEndpoints((endpoint, route) =>
        {
            if (route.Metadata?.ContainsKey(ReadOnlyQueryKey) == true)
            {
                endpoint.WithMetadata(new ReadOnlyQueryAttribute());
            }
        });
        return endpoints;
    }

    /// <summary>
    /// The gateway token instead of the client's Authorization header, and the Cookie header without the host's
    /// session cookie. The authorization step already refused a request without a signed-in user.
    /// </summary>
    private static ValueTask AddGatewayToken(RequestTransformContext transform)
    {
        var headers = transform.ProxyRequest.Headers;
        headers.Remove("Authorization");
        var user = transform.HttpContext.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var tokens = transform.HttpContext.RequestServices.GetRequiredService<GatewayTokens>();
            headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Create(user));
        }

        if (headers.TryGetValues("Cookie", out var values))
        {
            var cookies = WithoutSessionCookie(values);
            headers.Remove("Cookie");
            if (cookies.Length > 0)
            {
                headers.TryAddWithoutValidation("Cookie", cookies);
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>The cookies of the Cookie header(s), without the session cookie, in one header value.</summary>
    public static string WithoutSessionCookie(IEnumerable<string> cookieHeaders) =>
        string.Join("; ", cookieHeaders
            .SelectMany(header => header.Split(';'))
            .Select(cookie => cookie.Trim())
            .Where(cookie => cookie.Length > 0 &&
                             !cookie.StartsWith(HestiaUser.SessionCookieName + "=", StringComparison.Ordinal)));

    /// <summary>
    /// Module endpoints whose path is also a finance path. The module wins (route order), so finance never gets these
    /// requests. The host logs a warning at start for each one (T03).
    /// </summary>
    public static IReadOnlyList<string> Overlaps(IEnumerable<EndpointDataSource> dataSources, IEnumerable<string> financePaths)
    {
        var paths = financePaths.ToList();
        return dataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<RouteModel>() is null)
            .Select(endpoint => "/" + (endpoint.RoutePattern.RawText ?? string.Empty).Trim('/'))
            .Where(route => paths.Any(path =>
                route.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                route.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Logs a warning when the host starts, for each module endpoint on a finance path.</summary>
    public static void WarnAboutOverlaps(WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var options = app.Services.GetRequiredService<IOptions<FinanceProxyOptions>>().Value;
            var dataSources = ((IEndpointRouteBuilder)app).DataSources;
            foreach (var route in Overlaps(dataSources, options.Paths))
            {
                app.Logger.LogWarning(
                    "The module endpoint {Route} is on a finance path ({Section}:Paths). The module gets these requests, " +
                    "not finance. Remove the path from {Section}:Paths, or give the module endpoint another path",
                    route, FinanceProxyOptions.SectionName, FinanceProxyOptions.SectionName);
            }
        });
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
            // The read-only queries: exact paths, POST only, before the prefix routes. Same cluster and transforms.
            .Concat(options.ReadOnlyQueries.Select((path, index) => new RouteConfig
            {
                RouteId = $"finance-query-{index}",
                ClusterId = ClusterId,
                Order = RouteOrder - 1,
                Match = new RouteMatch { Path = path, Methods = ["POST"] },
                Metadata = new Dictionary<string, string> { [ReadOnlyQueryKey] = "true" },
                Transforms =
                [
                    new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" }
                ]
            }))
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

/// <summary>Reports every invalid <see cref="FinanceProxyOptions"/> setting, so the start error names them.</summary>
internal sealed class FinanceProxyOptionsValidator : IValidateOptions<FinanceProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, FinanceProxyOptions options)
    {
        var errors = options.Validate().ToList();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
