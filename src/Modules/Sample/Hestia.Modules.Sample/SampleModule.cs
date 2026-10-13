using Hestia.Modules.Sample.Application;
using Hestia.Modules.Sample.Contracts;
using Hestia.Modules.Sample.Endpoints;
using Hestia.Modules.Sample.Infrastructure;
using Hestia.Platform.Modules;
using Hestia.Platform.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Sample;

/// <summary>The Sample module: the template for new modules (see docs/new-module.md).</summary>
public sealed class SampleModule : IModule
{
    public string Name => "Sample";

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSampleModule(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapSampleModule();
}

public static class SampleModuleExtensions
{
    /// <summary>True: the Sample module stores its events in its outbox (durable events, T38).</summary>
    public const string DurableEventsSetting = "Sample:DurableEvents";

    /// <summary>Registers everything the Sample module needs, with one call.</summary>
    public static IServiceCollection AddSampleModule(this IServiceCollection services, IConfiguration configuration)
    {
        // Module events are in memory after the commit by default. The Sample module shows both modes: the setting
        // Sample:DurableEvents (default false) turns on durable events, through the module's outbox (T38).
        // A module that must never lose an event calls database.UseDurableEvents() with no setting.
        services.AddModuleDbContext<SampleDbContext>(SampleDbContext.SchemaName,
            database => database.UseDurableEvents(configuration.GetValue<bool>(DurableEventsSetting)));
        services.AddScoped<SampleItemService>();
        services.AddScoped<ISampleItemQueries, SampleItemQueries>();
        // Event consumers (SampleItemCreatedConsumer) need no registration: Wolverine finds them in the module assembly.
        services.AddScoped<SampleActivityQueries>();
        return services;
    }

    /// <summary>Maps the Sample endpoints under /api/sample.</summary>
    public static IEndpointRouteBuilder MapSampleModule(this IEndpointRouteBuilder endpoints)
    {
        SampleEndpoints.Map(endpoints);
        return endpoints;
    }
}
