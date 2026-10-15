using Hestia.Modules.Inbox.Application;
using Hestia.Modules.Inbox.Endpoints;
using Hestia.Modules.Inbox.Infrastructure;
using Hestia.Modules.Search.Contracts;
using Hestia.Modules.Today.Contracts;
using Hestia.Platform.Modules;
using Hestia.Platform.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Inbox;

/// <summary>
/// The Inbox module (T18): quick notes kept to sort later, a tile on Today, and the notes in the search. The clients
/// sort a note into another module themselves, then mark it done here. The module has no events.
/// </summary>
public sealed class InboxModule : IModule
{
    public string Name => "Inbox";

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddInboxModule(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapInboxModule();
}

public static class InboxModuleExtensions
{
    /// <summary>Registers everything the Inbox module needs, with one call.</summary>
    public static IServiceCollection AddInboxModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<InboxDbContext>(InboxDbContext.SchemaName);
        services.AddScoped<InboxService>();
        services.AddScoped<ITodayCardProvider, InboxTodayCards>();
        services.AddScoped<ISearchSource, InboxSearch>();
        return services;
    }

    /// <summary>Maps the Inbox endpoints under /api/inbox.</summary>
    public static IEndpointRouteBuilder MapInboxModule(this IEndpointRouteBuilder endpoints)
    {
        InboxEndpoints.Map(endpoints);
        return endpoints;
    }
}
