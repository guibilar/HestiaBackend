using Hestia.Modules.Alerts.Contracts;
using Hestia.Platform.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Hestia.Modules.Alerts.Endpoints;

/// <summary>The Alerts endpoint, under /api/alerts.</summary>
public static class AlertEndpoints
{
    public const string Prefix = "/api/alerts";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        // A GET, so a read-only user can call it too.
        endpoints.MapGet(Prefix, async Task<Ok<AlertList>> (IAlertQueries alerts, CancellationToken cancellationToken,
                bool refresh = false, string? order = null) =>
                TypedResults.Ok(await alerts.GetOpenAsync(new AlertListOptions(refresh, ParseOrder(order)), cancellationToken)))
            .WithName("GetAlerts")
            .WithTags("Alerts")
            .WithSummary("The open \"Needs attention\" items of all modules.")
            .WithDescription(
                "Most severe first, then newest first; order=module gives the Today order (module order, then the order of " +
                "each module's rules, T74). Each alert has the fields of a Today attention card, and raisedAt. " +
                "The host checks the rules in the background; refresh=true checks them first (at most Alerts:RefreshWait).");
    }

    /// <summary><c>severity</c> (or nothing) or <c>module</c> (T74).</summary>
    /// <exception cref="InvalidInputException">Another value.</exception>
    private static AlertOrder ParseOrder(string? order) => order?.Trim().ToLowerInvariant() switch
    {
        null or "" or "severity" => AlertOrder.Severity,
        "module" => AlertOrder.Module,
        _ => throw new InvalidInputException("order", "Use order=severity or order=module.")
    };
}
