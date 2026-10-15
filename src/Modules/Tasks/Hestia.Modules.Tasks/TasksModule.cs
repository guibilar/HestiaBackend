using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Tasks.Application;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Modules.Tasks.Endpoints;
using Hestia.Modules.Tasks.Infrastructure;
using Hestia.Modules.Today.Contracts;
using Hestia.Platform.Hosting;
using Hestia.Platform.Modules;
using Hestia.Platform.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Tasks;

/// <summary>
/// The Tasks module (T19): to-dos and repeated home tasks, with due dates, tags, completions, and links to documents and
/// finance transactions. Overdue tasks are alerts; Today shows a tile with the tasks due today.
/// </summary>
public sealed class TasksModule : IModule
{
    public string Name => "Tasks";

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddTasksModule(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapTasksModule();
}

public static class TasksModuleExtensions
{
    /// <summary>Registers everything the Tasks module needs, with one call.</summary>
    public static IServiceCollection AddTasksModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TasksOptions>()
            .Bind(configuration.GetSection(TasksOptions.SectionName))
            // The shared zone (Hestia:TimeZone, T85), unless Tasks:TimeZone overrides it.
            .PostConfigure(options => options.TimeZone = HestiaTimeZone.For(configuration, TasksOptions.SectionName))
            .Validate(options => options.IsValid(), TasksOptions.ValidationMessage)
            .ValidateOnStart();

        // Durable events from the first migration (T38): Timeline (T16) depends on them.
        services.AddModuleDbContext<TasksDbContext>(TasksDbContext.SchemaName, database => database.UseDurableEvents());
        services.AddSingleton<TaskDays>();
        services.AddScoped<TaskLinkTargets>();
        services.AddScoped<TaskService>();
        services.AddScoped<ITaskQueries, TaskQueries>();

        // The Today tile (T08), and the overdue alerts (T15): the Alerts module runs them, and Today shows them. After a
        // task event, TaskAlertsConsumer asks for a check (Wolverine finds it; it needs no registration).
        services.AddScoped<ITodayCardProvider, TasksTodayCards>();
        services.AddScoped<IAlertSource, TaskDueAlerts>();
        return services;
    }

    /// <summary>Maps the Tasks endpoints under /api/tasks.</summary>
    public static IEndpointRouteBuilder MapTasksModule(this IEndpointRouteBuilder endpoints)
    {
        TaskEndpoints.Map(endpoints);
        return endpoints;
    }
}
