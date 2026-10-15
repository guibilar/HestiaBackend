using Hestia.Modules.Journal.Application;
using Hestia.Modules.Journal.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Hestia.Modules.Journal.Endpoints;

/// <summary>The Journal endpoints, under /api/journal. A read-only user can call the GET endpoints only.</summary>
public static class JournalEndpoints
{
    public const string Prefix = "/api/journal";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var journal = endpoints.MapGroup(Prefix).WithTags("Journal");

        journal.MapGet("/summary", (JournalService service, CancellationToken cancellationToken) => service.SummaryAsync(cancellationToken))
            .WithName("GetJournalSummary")
            .WithSummary("Today's entry, the streak, the last 7 days, and \"On this day\" (the same day in earlier years).");

        journal.MapGet("/entries", (JournalService service, CancellationToken cancellationToken, string? q, DateOnly? from, DateOnly? to,
                int page = 1, int pageSize = 20) =>
                service.ListAsync(new JournalQuery(q, from, to, page, pageSize), cancellationToken))
            .WithName("ListJournalEntries")
            .WithSummary("A page of entries, newest first (with q: best match first).")
            .WithDescription("q: words in the text (web search syntax: \"a phrase\", -not). from, to: days, both included.");

        journal.MapGet("/entries/{day}", (DateOnly day, JournalService service, CancellationToken cancellationToken) =>
                service.GetAsync(day, cancellationToken))
            .WithName("GetJournalEntry")
            .WithSummary("The entry of one day (yyyy-MM-dd). 404 when the day has none.");

        journal.MapPut("/entries/{day}", async Task<Results<Created<JournalEntryDto>, Ok<JournalEntryDto>>> (DateOnly day,
                SaveJournalEntryRequest request, JournalService service, CancellationToken cancellationToken) =>
            {
                var (entry, created) = await service.SaveAsync(day, request, cancellationToken);
                return created
                    ? TypedResults.Created($"{Prefix}/entries/{day:yyyy-MM-dd}", entry)
                    : TypedResults.Ok(entry);
            })
            .WithName("SaveJournalEntry")
            .WithSummary("Writes the whole text of one day: 201 for a new entry, 200 for a change.")
            .WithDescription("The text is required (at most 20000 characters). A day after today is refused (400).");

        journal.MapDelete("/entries/{day}", async Task<NoContent> (DateOnly day, JournalService service, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(day, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteJournalEntry")
            .WithSummary("Deletes the entry of one day.");
    }
}
