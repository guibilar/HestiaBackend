using Hestia.Modules.Search.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hestia.Modules.Search.Endpoints;

/// <summary>The Search endpoint, under /api/search. It only reads, so a read-only user can call it.</summary>
public static class SearchEndpoints
{
    public const string Prefix = "/api/search";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var search = endpoints.MapGroup(Prefix).WithTags("Search");

        search.MapGet("/", (SearchService service, CancellationToken cancellationToken, string? q, string? module, int? limit) =>
                service.SearchAsync(q, module, limit, cancellationToken))
            .WithName("Search")
            .WithSummary("One search across all modules: the best hits of each module, in module order.")
            .WithDescription("q: 2 to 200 characters. module: only one module (for example Documents), with more hits. " +
                "limit: the hits of each module, 1 to 50 (5; 20 with module).");
    }
}
