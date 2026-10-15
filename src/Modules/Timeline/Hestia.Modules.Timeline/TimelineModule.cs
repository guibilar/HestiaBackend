using Hestia.Modules.Timeline.Application;
using Hestia.Modules.Timeline.Application.Finance;
using Hestia.Modules.Timeline.Contracts;
using Hestia.Modules.Timeline.Endpoints;
using Hestia.Platform.Hosting;
using Hestia.Platform.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Timeline;

/// <summary>
/// The Timeline module (T16): what happened in all modules, day by day. It keeps no data: each module gives its entries
/// through <see cref="ITimelineSource"/>, and Money's come from finance (<see cref="FinanceTimelineSource"/>).
/// </summary>
public sealed class TimelineModule : IModule
{
    public string Name => "Timeline";

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddTimelineModule(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapTimelineModule();
}

public static class TimelineModuleExtensions
{
    /// <summary>Registers everything the Timeline module needs, with one call.</summary>
    public static IServiceCollection AddTimelineModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TimelineOptions>()
            .Bind(configuration.GetSection(TimelineOptions.SectionName))
            // The shared zone (Hestia:TimeZone, T85), unless Timeline:TimeZone overrides it.
            .PostConfigure(options => options.TimeZone = HestiaTimeZone.For(configuration, TimelineOptions.SectionName))
            .Validate(options => options.IsValid(), TimelineOptions.ValidationMessage)
            .ValidateOnStart();
        services.AddScoped<TimelineService>();
        services.AddScoped<ITimelineSource, FinanceTimelineSource>();
        return services;
    }

    /// <summary>Maps the Timeline endpoint under /api/timeline.</summary>
    public static IEndpointRouteBuilder MapTimelineModule(this IEndpointRouteBuilder endpoints)
    {
        TimelineEndpoints.Map(endpoints);
        return endpoints;
    }
}
