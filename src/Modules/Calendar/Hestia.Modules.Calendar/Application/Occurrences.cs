using Hestia.Modules.Calendar.Contracts;
using Hestia.Modules.Calendar.Domain;
using Hestia.Platform.Exceptions;
using Ical.Net;
using Ical.Net.DataTypes;

namespace Hestia.Modules.Calendar.Application;

/// <summary>One occurrence of an event: its days, and its times for an event with a time.</summary>
public sealed record Occurrence(DateOnly StartDate, DateOnly EndDate, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);

/// <summary>
/// The occurrences of an event in a range (T103). A repeated event repeats with its iCalendar RRULE (Ical.Net), at the
/// same local time in its own time zone, also over a change of summer time.
/// </summary>
public static class Occurrences
{
    /// <summary>The most occurrences of one event in one answer: a daily event for a long range stays small.</summary>
    public const int MaxPerEvent = 500;

    /// <summary>Repeats faster than daily are refused: a calendar of a home does not need them.</summary>
    private static readonly HashSet<FrequencyType> Allowed =
        [FrequencyType.Daily, FrequencyType.Weekly, FrequencyType.Monthly, FrequencyType.Yearly];

    /// <summary>The checked rule in its normal form (no "RRULE:"), or null for no repeat.</summary>
    /// <exception cref="InvalidInputException">A rule that Ical.Net cannot read, or a repeat faster than daily.</exception>
    public static string? CheckRepeat(string? repeat)
    {
        var text = repeat?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        if (text.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[6..];
        }
        RecurrencePattern pattern;
        try
        {
            pattern = new RecurrencePattern(text);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            throw new InvalidInputException("repeat", $"\"{repeat}\" is not an iCalendar RRULE (for example FREQ=WEEKLY;BYDAY=MO).");
        }
        if (!Allowed.Contains(pattern.Frequency))
        {
            throw new InvalidInputException("repeat", "An event can repeat daily, weekly, monthly, or yearly.");
        }
        if (pattern.Interval is < 1 or > 1000)
        {
            throw new InvalidInputException("repeat", "INTERVAL must be from 1 to 1000.");
        }
        var normal = pattern.ToString() ?? text;
        if (normal.Length > CalendarLimits.MaxRepeatLength)
        {
            throw new InvalidInputException("repeat", $"The repeat can have at most {CalendarLimits.MaxRepeatLength} characters.");
        }
        return normal;
    }

    /// <summary>The occurrences of the event that touch a day of the range, in order.</summary>
    public static IReadOnlyList<Occurrence> In(CalendarEvent calendarEvent, CalendarRange range)
    {
        if (calendarEvent.AllDay)
        {
            if (calendarEvent.StartDate is not { } start)
            {
                return [];
            }
            var end = calendarEvent.EndDate is { } e && e >= start ? e : start;
            if (calendarEvent.Repeat is null)
            {
                return start <= range.To && end >= range.From ? [new Occurrence(start, end, null, null)] : [];
            }
            return RepeatedAllDay(calendarEvent, start, end.DayNumber - start.DayNumber, range);
        }

        if (calendarEvent.StartsAt is not { } startsAt)
        {
            return [];
        }
        var endsAt = calendarEvent.EndsAt is { } x && x >= startsAt ? x : startsAt;
        if (calendarEvent.Repeat is null)
        {
            // An event that ends exactly at the start of the range does not touch it; one of no length does on its day.
            var touches = startsAt < range.End && (endsAt > range.Start || (endsAt == startsAt && startsAt >= range.Start));
            return touches ? [Timed(startsAt, endsAt, range.TimeZone)] : [];
        }
        return RepeatedTimed(calendarEvent, startsAt, endsAt - startsAt, range);
    }

    private static List<Occurrence> RepeatedAllDay(CalendarEvent calendarEvent, DateOnly start, int extraDays, CalendarRange range)
    {
        var skipped = SkippedDates.Read(calendarEvent.SkippedDates).ToHashSet();
        var pattern = new Ical.Net.CalendarComponents.CalendarEvent
        {
            DtStart = new CalDateTime(start),
            DtEnd = new CalDateTime(start.AddDays(extraDays + 1)),
            RecurrenceRule = new RecurrencePattern(calendarEvent.Repeat!)
        };
        var list = new List<Occurrence>();
        // An occurrence of several days that starts before the range can still touch it.
        var from = range.From.AddDays(-extraDays);
        foreach (var occurrence in pattern.GetOccurrences(new CalDateTime(from < start ? start : from)))
        {
            var day = DateOnly.FromDateTime(occurrence.Period.StartTime.Value);
            if (day > range.To || list.Count >= MaxPerEvent)
            {
                break;
            }
            if (!skipped.Contains(day))
            {
                list.Add(new Occurrence(day, day.AddDays(extraDays), null, null));
            }
        }
        return list;
    }

    private static List<Occurrence> RepeatedTimed(CalendarEvent calendarEvent, DateTimeOffset startsAt, TimeSpan length, CalendarRange range)
    {
        var skipped = SkippedDates.Read(calendarEvent.SkippedDates).ToHashSet();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(calendarEvent.TimeZone);
        var localStart = TimeZoneInfo.ConvertTime(startsAt, zone).DateTime;
        // The repeat runs on the local clock with no zone (a floating time), and this machine's time zone data
        // (TimeZoneInfo) makes each local start a moment. Ical.Net has its own, older copy of the zone rules (NodaTime);
        // with it, a zone that changed its rules (Alberta in 2026) would be an hour off.
        var pattern = new Ical.Net.CalendarComponents.CalendarEvent
        {
            DtStart = new CalDateTime(localStart, tzId: null),
            RecurrenceRule = new RecurrencePattern(calendarEvent.Repeat!)
        };
        var list = new List<Occurrence>();
        // An occurrence that starts before the range and ends in it touches it too.
        var from = range.Start - length - TimeSpan.FromDays(1);
        var localFrom = from < startsAt ? localStart : TimeZoneInfo.ConvertTime(from, zone).DateTime;
        foreach (var occurrence in pattern.GetOccurrences(new CalDateTime(localFrom, tzId: null)))
        {
            var local = DateTime.SpecifyKind(occurrence.Period.StartTime.Value, DateTimeKind.Unspecified);
            var start = Moment(local, zone);
            if (start >= range.End || list.Count >= MaxPerEvent)
            {
                break;
            }
            var end = start + length;
            var touches = end > range.Start || (length == TimeSpan.Zero && start >= range.Start);
            // A skip is the local day of the occurrence in the time zone of the event.
            if (touches && !skipped.Contains(DateOnly.FromDateTime(local)))
            {
                list.Add(Timed(start, end, range.TimeZone));
            }
        }
        return list;
    }

    /// <summary>A local time in a zone as a moment. A time in the gap of a change to summer time moves to after the gap.</summary>
    public static DateTimeOffset Moment(DateTime local, TimeZoneInfo zone)
    {
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>
    /// An occurrence with a time. Its days are local days of the host's zone; an end at midnight belongs to the day before.
    /// </summary>
    private static Occurrence Timed(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone)
    {
        var startDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
        var lastMoment = end > start ? end - TimeSpan.FromTicks(1) : end;
        var endDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(lastMoment, zone).DateTime);
        return new Occurrence(startDay, endDay < startDay ? startDay : endDay, start, end);
    }
}
