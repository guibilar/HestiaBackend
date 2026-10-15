namespace Hestia.Modules.Timeline.Contracts;

/// <summary>
/// One page of the Timeline (<c>GET /api/timeline</c>): the days from <see cref="From"/> to <see cref="To"/>, newest
/// first, each with its entries. Days without entries are left out.
/// </summary>
/// <param name="Today">Today, in the host's time zone.</param>
/// <param name="Earlier">The <c>before</c> of the next page (the day before which it ends): the day after the latest day
/// before <see cref="From"/> with an entry, so the next page does not start with empty weeks; <see cref="From"/> when
/// there is none (see <see cref="HasEarlier"/>).</param>
/// <param name="Modules">Each module that has a source, with the status of its entries on this page.</param>
/// <param name="HasEarlier">False when no module has an entry before <see cref="From"/>: the clients hide "Show
/// earlier". True when a module did not answer (it may have some).</param>
public sealed record TimelineDto(
    DateOnly Today,
    DateOnly From,
    DateOnly To,
    DateOnly Earlier,
    IReadOnlyList<TimelineDayDto> Days,
    IReadOnlyList<TimelineModuleDto> Modules,
    bool HasEarlier = true);

/// <summary>One day, with its entries: those with a time newest first, then those of the whole day.</summary>
public sealed record TimelineDayDto(DateOnly Day, IReadOnlyList<TimelineEntry> Entries);

/// <summary>The status of a module's entries on one page.</summary>
public sealed record TimelineModuleDto(string Module, TimelineModuleStatus Status);

public enum TimelineModuleStatus
{
    /// <summary>All entries of the module are there.</summary>
    Complete,

    /// <summary>Some entries are missing (a part did not load).</summary>
    Partial,

    /// <summary>The module gave no entries: its source failed or did not answer in time.</summary>
    Failed
}
