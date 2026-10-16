using Hestia.Modules.Calendar.Application;
using Hestia.Modules.Calendar.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Hestia.Modules.Calendar.Endpoints;

/// <summary>The Calendar endpoints, under /api/calendar. A read-only user can call the GET endpoints only.</summary>
public static class CalendarEndpoints
{
    public const string Prefix = "/api/calendar";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var calendar = endpoints.MapGroup(Prefix).WithTags("Calendar");

        calendar.MapGet("/", (CalendarAgenda agenda, CancellationToken cancellationToken, DateOnly? from = null, DateOnly? to = null) =>
                agenda.GetAsync(from, to, cancellationToken))
            .WithName("GetCalendar")
            .WithSummary("The Calendar from one day to another (both included): the events, and the dated things of all modules.")
            .WithDescription("from: default today; to: default from + 30 days; at most 400 days. Items: events (with each " +
                "occurrence of a repeated event), tasks due, birthdays, reach-out days, loans due, documents that expire, " +
                "and warranties that end. On each day, the things of the whole day come first, then by time.");

        calendar.MapPost("/events", async Task<Created<CalendarEventDto>> (CalendarEventRequest request, CalendarService service,
                CancellationToken cancellationToken) =>
            {
                var calendarEvent = await service.CreateAsync(request, cancellationToken);
                return TypedResults.Created($"{Prefix}/events/{calendarEvent.Id}", calendarEvent);
            })
            .WithName("CreateCalendarEvent")
            .WithSummary("Adds an event: all day (startDate, endDate) or with a time (startsAt, endsAt), and a repeat (an RRULE).");

        calendar.MapGet("/events/{id:guid}", (Guid id, CalendarService service, CancellationToken cancellationToken) =>
                service.GetAsync(id, cancellationToken))
            .WithName("GetCalendarEvent");

        calendar.MapPut("/events/{id:guid}", (Guid id, CalendarEventRequest request, CalendarService service,
                CancellationToken cancellationToken) => service.UpdateAsync(id, request, cancellationToken))
            .WithName("UpdateCalendarEvent")
            .WithSummary("Changes an event (all values; all occurrences of a repeated event). The skipped days stay.");

        calendar.MapPost("/events/{id:guid}/skip", (Guid id, CalendarSkipRequest request, CalendarService service,
                CancellationToken cancellationToken) => service.SkipAsync(id, request, cancellationToken))
            .WithName("SkipCalendarOccurrence")
            .WithSummary("One occurrence of a repeated event does not happen (date: its day). The others stay.");

        calendar.MapDelete("/events/{id:guid}", async Task<NoContent> (Guid id, CalendarService service, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteCalendarEvent")
            .WithSummary("Deletes an event (all occurrences of a repeated event).");

        calendar.MapPost("/import", (CalendarImportRequest request, CalendarService service, CancellationToken cancellationToken) =>
                service.ImportAsync(request, cancellationToken))
            .WithName("ImportCalendar")
            .WithSummary("Imports the events of an .ics file (ics: its text). dryRun=true only says what would change.")
            .WithDescription("An event with a UID that Hestia has already (from an earlier import) is updated, not added again. " +
                "A cancelled event (STATUS:CANCELLED, METHOD:CANCEL) is deleted. Repeats faster than daily are skipped.");
    }
}
