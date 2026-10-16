using System.Security.Cryptography;
using System.Text;
using Hestia.Modules.Calendar.Contracts;
using Hestia.Platform.Exceptions;
using Ical.Net.DataTypes;

namespace Hestia.Modules.Calendar.Application;

/// <summary>One event of an <c>.ics</c> file, read into the fields of a Calendar event (or why it cannot be).</summary>
public sealed record IcsEvent(
    string Uid,
    string? RecurrenceId,
    DateOnly? RecurrenceDay,
    bool Cancelled,
    string Title,
    bool AllDay,
    DateOnly? StartDate,
    DateOnly? EndDate,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    string TimeZone,
    string? Location,
    string? Notes,
    string? Repeat,
    IReadOnlyList<DateOnly> SkippedDates,
    string? Problem);

/// <summary>
/// Reads an iCalendar file (RFC 5545, T103) with Ical.Net: its VEVENTs, with their time zones (IANA, or Windows names
/// from Outlook), repeats, and skipped occurrences (EXDATE). An event with a floating time (no zone) is in the host's
/// zone.
/// </summary>
public static class IcsReader
{
    /// <exception cref="InvalidInputException">Not an iCalendar text, too big, or too many events.</exception>
    public static IReadOnlyList<IcsEvent> Read(string? ics, TimeZoneInfo hostZone)
    {
        if (string.IsNullOrWhiteSpace(ics))
        {
            throw new InvalidInputException("ics", "Send the text of an .ics file.");
        }
        if (ics.Length > CalendarLimits.MaxIcsLength)
        {
            throw new InvalidInputException("ics", $"The file is too big (at most {CalendarLimits.MaxIcsLength / 1_000_000} MB).");
        }
        if (!ics.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidInputException("ics", "This is not an iCalendar (.ics) file: it has no BEGIN:VCALENDAR.");
        }

        Ical.Net.Calendar? calendar;
        try
        {
            calendar = Ical.Net.Calendar.Load(ics);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidInputException("ics", $"The .ics file could not be read: {exception.Message}");
        }
        if (calendar is null)
        {
            throw new InvalidInputException("ics", "The .ics file has no calendar.");
        }
        var events = calendar.Events.ToList();
        if (events.Count > CalendarLimits.MaxImportEvents)
        {
            throw new InvalidInputException("ics", $"The file has {events.Count} events; an import can have at most {CalendarLimits.MaxImportEvents}.");
        }
        var cancelAll = string.Equals(calendar.Method, "CANCEL", StringComparison.OrdinalIgnoreCase);
        return events.Select(e => ReadEvent(e, cancelAll, hostZone)).ToList();
    }

    private static IcsEvent ReadEvent(Ical.Net.CalendarComponents.CalendarEvent e, bool cancelAll, TimeZoneInfo hostZone)
    {
        var title = Cut(Clean(e.Summary), CalendarLimits.MaxTitleLength) ?? "(No title)";
        var location = Cut(Clean(e.Location), CalendarLimits.MaxLocationLength);
        var notes = Cut(Clean(e.Description), CalendarLimits.MaxNotesLength);
        var cancelled = cancelAll || string.Equals(e.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
        var uid = string.IsNullOrWhiteSpace(e.Uid) ? GeneratedUid(e) : Cut(e.Uid.Trim(), Domain.CalendarEvent.MaxUidLength)!;
        var zone = ZoneOf(e.DtStart, hostZone);

        string? recurrenceId = null;
        DateOnly? recurrenceDay = null;
        if (e.RecurrenceIdentifier?.StartTime is { } rid)
        {
            recurrenceId = rid.HasTime ? $"{rid.Value:yyyyMMdd'T'HHmmss}" : $"{rid.Value:yyyyMMdd}";
            recurrenceDay = LocalDay(rid, zone);
        }

        IcsEvent Problem(string problem) => new(uid, recurrenceId, recurrenceDay, cancelled, title, false, null, null, null, null,
            zone.Id, location, notes, null, [], problem);

        if (e.DtStart is not { } start)
        {
            return Problem("The event has no start (DTSTART).");
        }

        string? repeat = null;
        if (e.RecurrenceRule is { } rule)
        {
            try
            {
                repeat = Occurrences.CheckRepeat(rule.ToString());
            }
            catch (InvalidInputException exception)
            {
                return Problem(exception.Message);
            }
        }

        var allDay = !start.HasTime;
        var skipped = new List<DateOnly>();
        foreach (var exception in ExceptionDates(e))
        {
            if (LocalDay(exception, zone) is { } day)
            {
                skipped.Add(day);
            }
        }

        if (allDay)
        {
            var first = DateOnly.FromDateTime(start.Value);
            // DTEND of a whole-day event is the day after the last day.
            var last = e.DtEnd is { } end ? DateOnly.FromDateTime(end.Value).AddDays(-1) : first;
            if (last < first)
            {
                last = first;
            }
            return new IcsEvent(uid, recurrenceId, recurrenceDay, cancelled, title, true, first, last, null, null, zone.Id,
                location, notes, repeat, skipped, null);
        }

        if (Utc(start, zone) is not { } startsAt)
        {
            return Problem("The start time could not be read in its time zone.");
        }
        var endsAt = e.DtEnd is { } dtEnd && Utc(dtEnd, zone) is { } endUtc ? endUtc
            : e.Duration is { } duration ? startsAt + duration.ToTimeSpanUnspecified()
            : startsAt;
        if (endsAt < startsAt)
        {
            endsAt = startsAt;
        }
        return new IcsEvent(uid, recurrenceId, recurrenceDay, cancelled, title, false, null, null, startsAt, endsAt, zone.Id,
            location, notes, repeat, skipped, null);
    }

    /// <summary>All EXDATE values of the event.</summary>
    private static IEnumerable<CalDateTime> ExceptionDates(Ical.Net.CalendarComponents.CalendarEvent e) =>
        e.ExceptionDates.GetAllDates();

    /// <summary>
    /// The zone of a time: its TZID (IANA or Windows) when this machine knows it; else the host's zone (a floating time, a
    /// UTC time, or an unknown custom zone).
    /// </summary>
    private static TimeZoneInfo ZoneOf(CalDateTime? time, TimeZoneInfo hostZone)
    {
        var id = time?.TzId;
        if (string.IsNullOrWhiteSpace(id) || time!.IsUtc || string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            return hostZone;
        }
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
        {
            // A Windows name: keep the IANA name, which Ical.Net and the repeats use.
            if (!zone.HasIanaId && TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) &&
                TimeZoneInfo.TryFindSystemTimeZoneById(iana, out var ianaZone))
            {
                return ianaZone;
            }
            return zone;
        }
        return hostZone;
    }

    /// <summary>The moment of a time: in UTC as it is; a floating time or an unknown zone in <paramref name="zone"/>.</summary>
    private static DateTimeOffset? Utc(CalDateTime time, TimeZoneInfo zone)
    {
        if (time.IsUtc)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(time.Value, DateTimeKind.Utc));
        }
        var local = DateTime.SpecifyKind(time.Value, DateTimeKind.Unspecified);
        // A time in the gap of a change to summer time moves to after the gap.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static DateOnly? LocalDay(CalDateTime time, TimeZoneInfo zone)
    {
        if (!time.HasTime)
        {
            return DateOnly.FromDateTime(time.Value);
        }
        return Utc(time, zone) is { } moment ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, zone).DateTime) : null;
    }

    /// <summary>A UID for an event without one: the same event in a new import gets the same UID.</summary>
    private static string GeneratedUid(Ical.Net.CalendarComponents.CalendarEvent e)
    {
        var key = $"{e.Summary}|{e.DtStart}|{e.Location}";
        return "hestia-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
    }

    private static string? Clean(string? text)
    {
        var value = text?.Replace("\r\n", "\n").Replace('\0', ' ').Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? Cut(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];
}
