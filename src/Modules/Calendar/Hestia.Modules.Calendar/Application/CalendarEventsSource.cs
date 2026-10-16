using Hestia.Modules.Calendar.Contracts;
using Hestia.Modules.Calendar.Domain;
using Hestia.Modules.Calendar.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.Calendar.Application;

/// <summary>The events of the Calendar module itself, with each occurrence of a repeated event (T103).</summary>
public sealed class CalendarEventsSource(CalendarDbContext db) : ICalendarSource
{
    public const string ModuleName = "Calendar";

    public string Module => ModuleName;

    public async Task<CalendarItems> ListAsync(CalendarRange range, CancellationToken cancellationToken)
    {
        // A repeated event can have an occurrence in the range when it starts before the range ends. A single event
        // touches the range when it starts before the range ends and ends after it starts (an event of no length, on
        // the start of the range, too).
        var start = range.Start;
        var end = range.End;
        var events = await db.Events.AsNoTracking()
            .Where(e => e.Repeat != null
                ? (e.AllDay ? e.StartDate <= range.To : e.StartsAt < end)
                : (e.AllDay
                    ? e.StartDate <= range.To && e.EndDate >= range.From
                    : e.StartsAt < end && (e.EndsAt > start || e.EndsAt == e.StartsAt && e.StartsAt >= start)))
            .ToListAsync(cancellationToken);
        var items = new List<CalendarItem>();
        foreach (var calendarEvent in events)
        {
            foreach (var occurrence in Occurrences.In(calendarEvent, range))
            {
                items.Add(Item(calendarEvent, occurrence, range.TimeZone));
            }
        }
        return new CalendarItems(items);
    }

    public static CalendarItem Item(CalendarEvent e, Occurrence occurrence, TimeZoneInfo zone)
    {
        var repeated = e.Repeat is not null;
        var suffix = !repeated ? string.Empty
            : e.AllDay ? $".{occurrence.StartDate:yyyyMMdd}"
            : $".{TimeZoneInfo.ConvertTime(occurrence.StartsAt!.Value, zone):yyyyMMdd'T'HHmm}";
        return new CalendarItem($"calendar.event.{e.Id}{suffix}", ModuleName, "event", e.Title, e.Location, e.AllDay,
            occurrence.StartDate, occurrence.EndDate, occurrence.StartsAt, occurrence.EndsAt, $"/calendar?event={e.Id}",
            EventId: e.Id, Recurring: repeated);
    }
}
