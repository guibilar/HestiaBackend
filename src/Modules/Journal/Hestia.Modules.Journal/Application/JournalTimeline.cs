using Hestia.Modules.Journal.Infrastructure;
using Hestia.Modules.Timeline.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.Journal.Application;

/// <summary>The Journal entries of the Timeline (T16): the entry of each day, as its excerpt.</summary>
public sealed class JournalTimeline(JournalDbContext db) : ITimelineSource
{
    public string Module => JournalTodayCards.ModuleName;

    public async Task<TimelineEntries> ListAsync(TimelineRange range, CancellationToken cancellationToken)
    {
        var (from, to) = (range.From, range.To);
        var entries = await db.Entries.AsNoTracking().Where(e => e.Day >= from && e.Day <= to).ToListAsync(cancellationToken);
        return new TimelineEntries(entries.Select(e => new TimelineEntry(
            $"journal.entry.{e.Id:D}", Module, "entry", e.Day, null, "Journal", JournalService.Excerpt(e.Text), null,
            $"/journal?day={e.Day:yyyy-MM-dd}")).ToList());
    }
}
