using System.Security.Claims;
using Hestia.Modules.Today.Application;
using Hestia.Modules.Today.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Hestia.Modules.Today.Endpoints;

/// <summary>The Today endpoint, under /api/today.</summary>
public static class TodayEndpoints
{
    public const string Prefix = "/api/today";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        // A GET, so a read-only user can call it too.
        endpoints.MapGet(Prefix, async Task<Ok<TodayDto>> (ClaimsPrincipal user, TodayService today,
                CancellationToken cancellationToken, bool refresh = false) =>
                TypedResults.Ok(await today.GetAsync(user, refresh, cancellationToken)))
            .WithName("GetToday")
            .WithTags("Today")
            .WithSummary("All cards of the Today screen, from all modules.")
            .WithDescription(
                "The finance (Money) cards come first. A module that fails gives no cards and has the status Failed. " +
                "The host keeps a complete answer for a short time; refresh=true makes a new one (pull to refresh).");
    }
}
