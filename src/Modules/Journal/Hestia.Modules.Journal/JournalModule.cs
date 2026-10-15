using Hestia.Modules.Journal.Application;
using Hestia.Modules.Journal.Contracts;
using Hestia.Modules.Journal.Endpoints;
using Hestia.Modules.Journal.Infrastructure;
using Hestia.Modules.Search.Contracts;
using Hestia.Modules.Timeline.Contracts;
using Hestia.Modules.Today.Contracts;
using Hestia.Platform.Hosting;
using Hestia.Platform.Modules;
using Hestia.Platform.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Journal;

/// <summary>
/// The Journal module (T21): one entry per day, the streak, and "On this day" (the same day in earlier years), with a
/// full-text search and a tile on Today.
/// </summary>
public sealed class JournalModule : IModule
{
    public string Name => "Journal";

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddJournalModule(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapJournalModule();
}

public static class JournalModuleExtensions
{
    /// <summary>Registers everything the Journal module needs, with one call.</summary>
    public static IServiceCollection AddJournalModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JournalOptions>()
            .Bind(configuration.GetSection(JournalOptions.SectionName))
            // The shared zone (Hestia:TimeZone, T85), unless Journal:TimeZone overrides it.
            .PostConfigure(options => options.TimeZone = HestiaTimeZone.For(configuration, JournalOptions.SectionName))
            .Validate(options => options.IsValid(), JournalOptions.ValidationMessage)
            .ValidateOnStart();

        // Durable events (T38, T78): Timeline (T16) and Search (T17) use them.
        services.AddModuleDbContext<JournalDbContext>(JournalDbContext.SchemaName, database => database.UseDurableEvents());
        services.AddSingleton<JournalDays>();
        services.AddScoped<JournalService>();
        // Reads only the Journal tables.
        services.AddScoped<IJournalQueries, JournalQueries>();
        services.AddScoped<ITodayCardProvider, JournalTodayCards>();
        // The Timeline entries (T16).
        services.AddScoped<ITimelineSource, JournalTimeline>();
        // The search (T17).
        services.AddScoped<ISearchSource, JournalSearch>();
        return services;
    }

    /// <summary>Maps the Journal endpoints under /api/journal.</summary>
    public static IEndpointRouteBuilder MapJournalModule(this IEndpointRouteBuilder endpoints)
    {
        JournalEndpoints.Map(endpoints);
        return endpoints;
    }
}
