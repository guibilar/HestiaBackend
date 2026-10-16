using Hestia.Modules.Journal.Contracts;
using Hestia.Modules.Journal.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.Journal.Application;

/// <summary>The Journal data for other modules (<see cref="IJournalQueries"/>). It reads only the Journal tables.</summary>
public sealed class JournalQueries(JournalDbContext db) : IJournalQueries
{
    public async Task<JournalEntryDto?> FindAsync(DateOnly day, CancellationToken cancellationToken) =>
        await db.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.Day == day, cancellationToken) is { } entry
            ? JournalService.Dto(entry)
            : null;

    public async Task<IReadOnlyList<JournalEntrySummaryDto>> ListAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        (await db.Entries.AsNoTracking().Where(e => e.Day >= from && e.Day <= to).OrderByDescending(e => e.Day)
            .ToListAsync(cancellationToken)).Select(JournalService.Summary).ToList();

    public Task<IReadOnlyList<OnThisDayDto>> OnThisDayAsync(DateOnly today, CancellationToken cancellationToken) =>
        JournalService.OnThisDayAsync(db, today, cancellationToken);
}
