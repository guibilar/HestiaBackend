using Hestia.Modules.Calendar.Contracts;
using Hestia.Platform.Domain;

namespace Hestia.Modules.Calendar.Domain;

/// <summary>
/// An event of the Calendar (T103). An all-day event has <see cref="StartDate"/> and <see cref="EndDate"/>; an event
/// with a time has <see cref="StartsAt"/> and <see cref="EndsAt"/>. A repeated event has an iCalendar RRULE in
/// <see cref="Repeat"/>, and its first occurrence in the dates above.
/// </summary>
public class CalendarEvent : BaseEntity
{
    public const int MaxUidLength = 500;
    public const int MaxTimeZoneLength = 100;
    public const int MaxRecurrenceIdLength = 40;

    public string Title { get; set; } = string.Empty;
    public bool AllDay { get; set; }

    public DateOnly? StartDate { get; set; }

    /// <summary>The last day of an all-day event (included).</summary>
    public DateOnly? EndDate { get; set; }

    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>The IANA time zone: a repeated event repeats at the same local time in it.</summary>
    public string TimeZone { get; set; } = "UTC";

    public string? Location { get; set; }
    public string? Notes { get; set; }

    /// <summary>The iCalendar RRULE (without "RRULE:"), or null.</summary>
    public string? Repeat { get; set; }

    /// <summary>The occurrences that do not happen, as local days, stored as <c>|2026-10-22|2026-11-05|</c>, or empty.</summary>
    public string SkippedDates { get; set; } = string.Empty;

    public CalendarEventSource Source { get; set; }

    /// <summary>The iCalendar UID of an imported event. A new import of the same UID (and recurrence id) updates it.</summary>
    public string? Uid { get; set; }

    /// <summary>
    /// For one changed occurrence of an imported repeated event (RECURRENCE-ID): that occurrence, as text. Null for the
    /// event itself.
    /// </summary>
    public string? RecurrenceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The skipped days of an event, as stored in <see cref="CalendarEvent.SkippedDates"/>.</summary>
public static class SkippedDates
{
    public static IReadOnlyList<DateOnly> Read(string stored) =>
        stored.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(text => DateOnly.TryParseExact(text, "yyyy-MM-dd", out var day) ? (DateOnly?)day : null)
            .OfType<DateOnly>()
            .Distinct()
            .Order()
            .ToList();

    public static string Write(IEnumerable<DateOnly> days)
    {
        var list = days.Distinct().Order().ToList();
        return list.Count == 0 ? string.Empty : "|" + string.Join("|", list.Select(day => day.ToString("yyyy-MM-dd"))) + "|";
    }
}
