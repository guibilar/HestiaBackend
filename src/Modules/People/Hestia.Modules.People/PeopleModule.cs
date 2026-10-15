using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.People.Application;
using Hestia.Modules.People.Contracts;
using Hestia.Modules.People.Endpoints;
using Hestia.Modules.People.Infrastructure;
using Hestia.Platform.Modules;
using Hestia.Platform.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.People;

/// <summary>
/// The People module (T20): a small personal CRM. The people the user cares about, their contacts, a reach-out reminder,
/// birthdays, gift ideas, and things lent or borrowed, with links to documents, tasks, home items, and transactions.
/// </summary>
public sealed class PeopleModule : IModule
{
    public string Name => "People";

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddPeopleModule(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPeopleModule();
}

public static class PeopleModuleExtensions
{
    /// <summary>Registers everything the People module needs, with one call.</summary>
    public static IServiceCollection AddPeopleModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PeopleOptions>()
            .Bind(configuration.GetSection(PeopleOptions.SectionName))
            .Validate(options => options.IsValid(), PeopleOptions.ValidationMessage)
            .ValidateOnStart();

        // Durable events from the first migration (T38): Timeline (T16) and later link kinds in other modules use them.
        services.AddModuleDbContext<PeopleDbContext>(PeopleDbContext.SchemaName, database => database.UseDurableEvents());
        services.AddSingleton<PeopleDays>();
        services.AddScoped<PeopleReader>();
        services.AddScoped<PersonLinkTargets>();
        services.AddScoped<PersonService>();
        services.AddScoped<PeopleRecordService>();
        // Reads only the People tables.
        services.AddScoped<IPeopleQueries, PeopleQueries>();

        // The reminder alerts (T15): the Alerts module runs them, and Today shows them. After a People event,
        // PeopleAlertsConsumer asks for a check (Wolverine finds it; it needs no registration).
        services.AddScoped<IAlertSource, ReminderAlerts>();
        return services;
    }

    /// <summary>Maps the People endpoints under /api/people.</summary>
    public static IEndpointRouteBuilder MapPeopleModule(this IEndpointRouteBuilder endpoints)
    {
        PeopleEndpoints.Map(endpoints);
        return endpoints;
    }
}
