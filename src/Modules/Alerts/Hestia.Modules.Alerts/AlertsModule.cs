using Hestia.Modules.Alerts.Application;
using Hestia.Modules.Alerts.Application.Finance;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Alerts.Endpoints;
using Hestia.Modules.Alerts.Infrastructure;
using Hestia.Platform.Hosting;
using Hestia.Platform.Modules;
using Hestia.Platform.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Alerts;

/// <summary>
/// The Alerts module (T15): one "Needs attention" list from all modules. Each module adds its rules as an
/// <see cref="IAlertSource"/>; this module runs them in the background, stores the alerts (schema <c>alerts</c>), and
/// gives them to Today (<see cref="IAlertQueries"/>) and to the clients (<c>GET /api/alerts</c>).
/// </summary>
public sealed class AlertsModule : IModule
{
    public string Name => "Alerts";

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddAlertsModule(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapAlertsModule();
}

public static class AlertsModuleExtensions
{
    /// <summary>Registers everything the Alerts module needs, with one call.</summary>
    public static IServiceCollection AddAlertsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AlertsOptions>()
            .Bind(configuration.GetSection(AlertsOptions.SectionName))
            // The shared zone (Hestia:TimeZone, T85), unless Alerts:TimeZone overrides it.
            .PostConfigure(options => options.TimeZone = HestiaTimeZone.For(configuration, AlertsOptions.SectionName))
            .Validate(options => options.IsValid(), AlertsOptions.ValidationMessage)
            .ValidateOnStart();

        // The module raises no events, so it needs no outbox. Its consumers use the inbox (T36).
        services.AddModuleDbContext<AlertsDbContext>(AlertsDbContext.SchemaName);

        // Singletons registered by type, so the event consumers (Wolverine handlers) can take them.
        services.AddSingleton<AlertsRunner>();
        services.AddSingleton<IAlertChecks, AlertChecks>();
        services.AddSingleton<IAlertQueries, AlertQueries>();
        services.AddHostedService<AlertsWorker>();

        // Finance is outside the host, so its rules are here. Other modules register their own sources.
        services.AddScoped<IAlertSource, FinanceAlertSource>();
        // The finance event consumer (FinanceAlertsConsumer) needs no registration: Wolverine finds it.
        return services;
    }

    /// <summary>Maps the Alerts endpoint, /api/alerts.</summary>
    public static IEndpointRouteBuilder MapAlertsModule(this IEndpointRouteBuilder endpoints)
    {
        AlertEndpoints.Map(endpoints);
        return endpoints;
    }
}
