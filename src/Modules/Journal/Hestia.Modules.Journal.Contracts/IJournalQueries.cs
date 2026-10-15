namespace Hestia.Modules.Journal.Contracts;

/// <summary>What other modules can read from the Journal module (for example Timeline and Search).</summary>
public interface IJournalQueries
{
    /// <summary>The entry of one day, or null.</summary>
    Task<JournalEntryDto?> FindAsync(DateOnly day, CancellationToken cancellationToken);

    /// <summary>The entries from <paramref name="from"/> to <paramref name="to"/> (both included), newest first.</summary>
    Task<IReadOnlyList<JournalEntrySummaryDto>> ListAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
