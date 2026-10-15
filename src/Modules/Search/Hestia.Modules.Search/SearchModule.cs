using Hestia.Modules.Search.Application;
using Hestia.Modules.Search.Application.Finance;
using Hestia.Modules.Search.Contracts;
using Hestia.Modules.Search.Endpoints;
using Hestia.Platform.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Search;

/// <summary>
/// The Search module (T17): one search across all modules. It keeps no index (D15): each module searches its own data
/// through <see cref="ISearchSource"/>, and Money's hits come from finance (<see cref="FinanceSearchSource"/>).
/// </summary>
public sealed class SearchModule : IModule
{
    public string Name => "Search";

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSearchModule(configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapSearchModule();
}

public static class SearchModuleExtensions
{
    /// <summary>Registers everything the Search module needs, with one call.</summary>
    public static IServiceCollection AddSearchModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SearchOptions>()
            .Bind(configuration.GetSection(SearchOptions.SectionName))
            .Validate(options => options.SourceTimeout > TimeSpan.Zero, "Search: Search:SourceTimeout must be more than zero.")
            .ValidateOnStart();
        services.AddScoped<SearchService>();
        services.AddScoped<ISearchSource, FinanceSearchSource>();
        return services;
    }

    /// <summary>Maps the Search endpoint under /api/search.</summary>
    public static IEndpointRouteBuilder MapSearchModule(this IEndpointRouteBuilder endpoints)
    {
        SearchEndpoints.Map(endpoints);
        return endpoints;
    }
}
