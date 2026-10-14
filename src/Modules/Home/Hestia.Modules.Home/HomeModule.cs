using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Home.Application;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.Home.Endpoints;
using Hestia.Modules.Home.Infrastructure;
using Hestia.Platform.Modules;
using Hestia.Platform.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Home;

/// <summary>
/// The Home module (T22): the rooms and the inventory of the home (where each thing is, what it cost, its serial number,
/// and its warranty), with links to documents, tasks (maintenance), and finance transactions, a CSV export for an
/// insurance claim, and warranty alerts.
/// </summary>
public sealed class HomeModule : IModule
{
    public string Name => "Home";

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddHomeModule(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapHomeModule();
}

public static class HomeModuleExtensions
{
    /// <summary>Registers everything the Home module needs, with one call.</summary>
    public static IServiceCollection AddHomeModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HomeOptions>()
            .Bind(configuration.GetSection(HomeOptions.SectionName))
            .Validate(options => options.IsValid(), HomeOptions.ValidationMessage)
            .ValidateOnStart();

        // Durable events from the first migration (T38): Documents and Tasks remove their links on ItemDeleted.
        services.AddModuleDbContext<HomeDbContext>(HomeDbContext.SchemaName, database => database.UseDurableEvents());
        services.AddSingleton<HomeDays>();
        services.AddScoped<ItemLinkTargets>();
        services.AddScoped<ItemRelations>();
        services.AddScoped<RoomService>();
        services.AddScoped<ItemService>();
        services.AddScoped<InventoryExport>();
        // Reads only the Home tables: Documents and Tasks use it for their Home.Item links, and Home uses theirs.
        services.AddScoped<IHomeQueries, HomeQueries>();

        // The warranty alerts (T15): the Alerts module runs them, and Today shows them. After an item event,
        // HomeAlertsConsumer asks for a check (Wolverine finds it; it needs no registration).
        services.AddScoped<IAlertSource, WarrantyAlerts>();
        return services;
    }

    /// <summary>Maps the Home endpoints under /api/home.</summary>
    public static IEndpointRouteBuilder MapHomeModule(this IEndpointRouteBuilder endpoints)
    {
        HomeEndpoints.Map(endpoints);
        return endpoints;
    }
}
