using Hestia.Modules.Today.Application;
using Hestia.Modules.Today.Application.Finance;
using Hestia.Modules.Today.Contracts;
using Hestia.Modules.Today.Endpoints;
using Hestia.Platform.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Today;

/// <summary>
/// The Today module (T08): one endpoint with all cards of the Today screen. Each module gives its tiles through
/// <see cref="ITodayCardProvider"/>; the "Needs attention" items of all modules come from the Alerts module (T15).
/// This module has no data of its own, so it has no DbContext and no schema.
/// </summary>
public sealed class TodayModule : IModule
{
    public string Name => "Today";

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddTodayModule(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapTodayModule();
}

public static class TodayModuleExtensions
{
    /// <summary>Registers everything the Today module needs, with one call.</summary>
    public static IServiceCollection AddTodayModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TodayOptions>()
            .Bind(configuration.GetSection(TodayOptions.SectionName))
            .Validate(options => options.IsValid(),
                "Today: set Today:TimeZone to an IANA time zone (for example America/Edmonton), Today:CacheDuration to " +
                "zero or more, and Today:ProviderTimeout to more than zero.")
            .ValidateOnStart();
        services.AddMemoryCache();
        services.AddScoped<TodayService>();
        // Finance is outside the host, so its provider is here. Other modules register their own providers.
        services.AddScoped<ITodayCardProvider, FinanceTodayCards>();
        return services;
    }

    /// <summary>Maps the Today endpoint, /api/today.</summary>
    public static IEndpointRouteBuilder MapTodayModule(this IEndpointRouteBuilder endpoints)
    {
        TodayEndpoints.Map(endpoints);
        return endpoints;
    }
}
