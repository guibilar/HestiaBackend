namespace Hestia.Modules.Journal.Contracts;

/// <summary>What other modules can read from the Journal module (for example Timeline and Search).</summary>
public interface IJournalQueries
{
    /// <summary>The entry of one day, or null.</summary>
    Task<JournalEntryDto?> FindAsync(DateOnly day, CancellationToken cancellationToken);

    /// <summary>The entries from <paramref name="from"/> to <paramref name="to"/> (both included), newest first.</summary>
    Task<IReadOnlyList<JournalEntrySummaryDto>> ListAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>
    /// The entries of the same day in earlier years ("On this day", for Today, T106), newest first. On Feb 28 of a year
    /// that is not a leap year, Feb 29 counts too.
    /// </summary>
    Task<IReadOnlyList<OnThisDayDto>> OnThisDayAsync(DateOnly today, CancellationToken cancellationToken);
}
