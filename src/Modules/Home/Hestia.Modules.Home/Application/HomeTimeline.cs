using Hestia.Modules.Home.Infrastructure;
using Hestia.Modules.Timeline.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.Home.Application;

/// <summary>The Home entries of the Timeline (T16): each item added to the inventory, with its room.</summary>
public sealed class HomeTimeline(HomeDbContext db) : ITimelineSource
{
    public string Module => "Home";

    public async Task<TimelineEntries> ListAsync(TimelineRange range, CancellationToken cancellationToken)
    {
        var start = range.Start;
        var end = range.End;
        var items = await db.Items.AsNoTracking()
            .Where(i => i.CreatedAt >= start && i.CreatedAt < end)
            .Select(i => new { i.Id, i.Name, i.RoomId, i.CreatedAt })
            .ToListAsync(cancellationToken);
        var roomIds = items.Where(i => i.RoomId != null).Select(i => i.RoomId!.Value).Distinct().ToList();
        var rooms = await db.Rooms.AsNoTracking().Where(r => roomIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken);
        return new TimelineEntries(items.Select(i => new TimelineEntry(
            $"home.item-added.{i.Id:D}", Module, "item-added", range.DayOf(i.CreatedAt), i.CreatedAt, i.Name,
            i.RoomId is { } room && rooms.TryGetValue(room, out var roomName) ? $"Added to {roomName}" : "Added to the inventory",
            null, $"/home/items/{i.Id:D}")).ToList());
    }
}
