namespace Hestia.Modules.Calendar.Contracts;

/// <summary>
/// The dated things of one module for the Calendar (T103): a task due, a birthday, a warranty that ends. A module that
/// has them implements it, and registers it in its <c>AddServices</c> as a scoped service:
/// <c>services.AddScoped&lt;ICalendarSource, TaskCalendar&gt;()</c>. The Calendar module calls all sources at the same
/// time for each <c>GET /api/calendar</c>, inside the HTTP request of the user.
/// </summary>
/// <remarks>
/// A source reads its own data for the range (no copy is kept), so a change shows at once. A source that throws, or does
/// not answer in time (<c>Calendar:SourceTimeout</c>), gives no items; the other modules still show theirs. A source
/// that loads several things can return what it has, with <see cref="CalendarItems.Complete"/> false.
/// </remarks>
public interface ICalendarSource
{
    /// <summary>The module name, for example <c>Tasks</c>. The same in every item.</summary>
    string Module { get; }

    /// <summary>The items that touch a day of <paramref name="range"/>, in any order.</summary>
    Task<CalendarItems> ListAsync(CalendarRange range, CancellationToken cancellationToken);
}

/// <summary>The days of one Calendar view, in the time zone of the host (<c>Hestia:TimeZone</c>).</summary>
/// <param name="From">The first day (included).</param>
/// <param name="To">The last day (included).</param>
public sealed record CalendarRange(DateOnly From, DateOnly To, TimeZoneInfo TimeZone)
{
    /// <summary>The start of <see cref="From"/> (local midnight), as a moment in UTC (PostgreSQL takes only UTC).</summary>
    public DateTimeOffset Start => Moment(From);

    /// <summary>The end of <see cref="To"/>: the start of the next day (not included), as a moment in UTC.</summary>
    public DateTimeOffset End => Moment(To.AddDays(1));

    /// <summary>The local day of a moment.</summary>
    public DateOnly DayOf(DateTimeOffset moment) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, TimeZone).DateTime);

    /// <summary>True when the day is in the range.</summary>
    public bool Contains(DateOnly day) => day >= From && day <= To;

    /// <summary>The start of a local day, as a moment in UTC.</summary>
    public DateTimeOffset Moment(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        // A day that starts in a gap of a time change starts at the end of the gap.
        while (TimeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }
        return new DateTimeOffset(local, TimeZone.GetUtcOffset(local)).ToUniversalTime();
    }
}

/// <param name="Items">The items of the module.</param>
/// <param name="Complete">False when some data did not load, so some items are missing.</param>
public sealed record CalendarItems(IReadOnlyList<CalendarItem> Items, bool Complete = true);

/// <summary>Reads the Calendar for other modules, for example the agenda on Today (T106).</summary>
public interface ICalendarQueries
{
    /// <summary>The items of all modules from <paramref name="from"/> to <paramref name="to"/> (both included).</summary>
    Task<CalendarDto> GetAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>Today, in the time zone of the Calendar module.</summary>
    DateOnly Today { get; }
}
