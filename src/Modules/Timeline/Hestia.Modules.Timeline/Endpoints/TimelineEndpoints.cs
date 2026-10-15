using Hestia.Modules.Timeline.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hestia.Modules.Timeline.Endpoints;

/// <summary>The Timeline endpoint, under /api/timeline. It only reads, so a read-only user can call it.</summary>
public static class TimelineEndpoints
{
    public const string Prefix = "/api/timeline";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var timeline = endpoints.MapGroup(Prefix).WithTags("Timeline");

        timeline.MapGet("/", (TimelineService service, CancellationToken cancellationToken, DateOnly? before, string? module,
                int days = 7) =>
                service.GetAsync(before, days, module, cancellationToken))
            .WithName("GetTimeline")
            .WithSummary("What happened in all modules, day by day, newest first: one page of days.")
            .WithDescription("before: the page ends the day before it (none: the page ends today; the next page uses the " +
                "answer's earlier). days: 1 to 31 (7). module: only one module, for example Money.");
    }
}
