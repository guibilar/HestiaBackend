using Hestia.Modules.Documents.Infrastructure;
using Hestia.Modules.Timeline.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.Documents.Application;

/// <summary>The Documents entries of the Timeline (T16): each document added, at the time it was added.</summary>
public sealed class DocumentTimeline(DocumentsDbContext db) : ITimelineSource
{
    public string Module => "Documents";

    public async Task<TimelineEntries> ListAsync(TimelineRange range, CancellationToken cancellationToken)
    {
        var start = range.Start;
        var end = range.End;
        var documents = await db.Documents.AsNoTracking()
            .Where(d => d.CreatedAt >= start && d.CreatedAt < end)
            .Select(d => new { d.Id, d.Title, d.Kind, d.CreatedAt })
            .ToListAsync(cancellationToken);
        return new TimelineEntries(documents.Select(d => new TimelineEntry(
            $"documents.document-added.{d.Id:D}", Module, "document-added", range.DayOf(d.CreatedAt), d.CreatedAt,
            d.Title, d.Kind.ToString(), null, $"/documents/{d.Id:D}")).ToList());
    }
}
