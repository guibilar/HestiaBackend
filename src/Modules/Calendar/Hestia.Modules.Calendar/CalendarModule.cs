using Hestia.Modules.Calendar.Application;
using Hestia.Modules.Calendar.Contracts;
using Hestia.Modules.Calendar.Endpoints;
using Hestia.Modules.Calendar.Infrastructure;
using Hestia.Platform.Hosting;
using Hestia.Platform.Modules;
using Hestia.Platform.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Calendar;

/// <summary>
/// The Calendar module (T103): events with repeats, the import of <c>.ics</c> files, and one view of the dated things of
/// all modules (<see cref="ICalendarSource"/>). The module has no events.
/// </summary>
public sealed class CalendarModule : IModule
{
    public string Name => "Calendar";

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddCalendarModule(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapCalendarModule();
}

public static class CalendarModuleExtensions
{
    /// <summary>Registers everything the Calendar module needs, with one call.</summary>
    public static IServiceCollection AddCalendarModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CalendarOptions>()
            .Bind(configuration.GetSection(CalendarOptions.SectionName))
            // The shared zone (Hestia:TimeZone, T85), unless Calendar:TimeZone overrides it.
            .PostConfigure(options => options.TimeZone = HestiaTimeZone.For(configuration, CalendarOptions.SectionName))
            .Validate(options => options.IsValid(), CalendarOptions.ValidationMessage)
            .ValidateOnStart();

        services.AddModuleDbContext<CalendarDbContext>(CalendarDbContext.SchemaName);
        services.AddSingleton<CalendarDays>();
        services.AddScoped<CalendarService>();
        services.AddScoped<CalendarAgenda>();
        services.AddScoped<ICalendarQueries>(provider => provider.GetRequiredService<CalendarAgenda>());
        // The events of this module are one source among the others.
        services.AddScoped<ICalendarSource, CalendarEventsSource>();
        return services;
    }

    /// <summary>Maps the Calendar endpoints under /api/calendar.</summary>
    public static IEndpointRouteBuilder MapCalendarModule(this IEndpointRouteBuilder endpoints)
    {
        CalendarEndpoints.Map(endpoints);
        return endpoints;
    }
}
