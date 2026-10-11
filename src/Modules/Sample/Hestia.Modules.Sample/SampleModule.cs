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
    /// <summary>Registers everything the Sample module needs, with one call.</summary>
    public static IServiceCollection AddSampleModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<SampleDbContext>(SampleDbContext.SchemaName);
        services.AddScoped<SampleItemService>();
        services.AddScoped<ISampleItemQueries, SampleItemQueries>();
        // Event consumers (SampleItemCreatedConsumer) need no registration: Wolverine finds them in the module assembly.
        services.AddSingleton<SampleActivity>();
        return services;
    }

    /// <summary>Maps the Sample endpoints under /api/sample.</summary>
    public static IEndpointRouteBuilder MapSampleModule(this IEndpointRouteBuilder endpoints)
    {
        SampleEndpoints.Map(endpoints);
        return endpoints;
    }
}
