namespace Hestia.Modules.Timeline.Contracts;

/// <summary>
/// The Timeline entries of one module (T16): what happened in it, day by day. A module that has entries implements it,
/// and registers it in its <c>AddServices</c> as a scoped service:
/// <c>services.AddScoped&lt;ITimelineSource, DocumentTimeline&gt;()</c>. The Timeline module calls all sources in
/// parallel for each <c>GET /api/timeline</c>, inside the HTTP request of the user.
/// </summary>
/// <remarks>
/// A source reads its own data for the range (no copy of it is kept), so a deleted thing goes from the Timeline at once.
/// A source that throws, or does not answer in time (<c>Timeline:SourceTimeout</c>), gives no entries; the other modules
/// still show theirs. A source that loads several things can return what it has, with
/// <see cref="TimelineEntries.Complete"/> false.
/// </remarks>
public interface ITimelineSource
{
    /// <summary>The module name, for example <c>Documents</c>. The same in every entry.</summary>
    string Module { get; }

    /// <summary>The entries of the days of <paramref name="range"/>, in any order.</summary>
    Task<TimelineEntries> ListAsync(TimelineRange range, CancellationToken cancellationToken);

    /// <summary>
    /// The latest day before <paramref name="range"/> (before its <see cref="TimelineRange.From"/>) with an entry of this
    /// module, or null when there is none. The Timeline uses it to skip the days without entries, and to know when there
    /// is nothing earlier.
    /// </summary>
    Task<DateOnly?> LatestBeforeAsync(TimelineRange range, CancellationToken cancellationToken);
}

/// <summary>The days of one Timeline page, in the time zone of the host (<c>Hestia:TimeZone</c>).</summary>
/// <param name="From">The first day (included).</param>
/// <param name="To">The last day (included).</param>
public sealed record TimelineRange(DateOnly From, DateOnly To, TimeZoneInfo TimeZone)
{
    /// <summary>The start of <see cref="From"/> (local midnight), as a moment in UTC (PostgreSQL takes only UTC).</summary>
    public DateTimeOffset Start => Moment(From);

    /// <summary>The end of <see cref="To"/>: the start of the next day (not included), as a moment in UTC.</summary>
    public DateTimeOffset End => Moment(To.AddDays(1));

    /// <summary>The local day of a moment.</summary>
    public DateOnly DayOf(DateTimeOffset moment) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, TimeZone).DateTime);

    /// <summary>True when the day is in the range.</summary>
    public bool Contains(DateOnly day) => day >= From && day <= To;

    private DateTimeOffset Moment(DateOnly day)
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

/// <summary>
/// One thing that happened: a document added, a task done, a transaction, a journal entry. The clients show
/// <see cref="Title"/> and <see cref="Text"/> as they are, and format <see cref="Amount"/> as money.
/// </summary>
/// <param name="Id">Stable for the same thing, and unique in the Timeline: the lower-case module name, a dot, the kind, a
/// dot, and the id (for example <c>documents.document-added.&lt;id&gt;</c>).</param>
/// <param name="Module">The module, for example <c>Documents</c>.</param>
/// <param name="Kind">What happened inside the module, for example <c>document-added</c>. The clients pick an icon by it.</param>
/// <param name="Day">The local day of the entry.</param>
/// <param name="At">When it happened, if the module knows the time; null for a thing of a whole day.</param>
/// <param name="Title">One line, for example the title of the document.</param>
/// <param name="Text">A second line, or null.</param>
/// <param name="Amount">An amount of money (money in is positive, money out negative), or null.</param>
/// <param name="Link">The web route of the thing (for example <c>/documents/&lt;id&gt;</c>); the Android app maps it.</param>
public sealed record TimelineEntry(
    string Id,
    string Module,
    string Kind,
    DateOnly Day,
    DateTimeOffset? At,
    string Title,
    string? Text,
    decimal? Amount,
    string? Link);

/// <param name="Entries">The entries of the module.</param>
/// <param name="Complete">False when some data did not load, so some entries are missing.</param>
public sealed record TimelineEntries(IReadOnlyList<TimelineEntry> Entries, bool Complete = true);
