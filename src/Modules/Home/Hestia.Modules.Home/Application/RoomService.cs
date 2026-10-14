using Hestia.Modules.Home.Contracts;
using Hestia.Modules.Home.Domain;
using Hestia.Modules.Home.Infrastructure;
using Hestia.Platform.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.Home.Application;

/// <summary>The rooms: add, rename, order, and delete (only a room without items).</summary>
public sealed class RoomService(HomeDbContext db, HomeDays days, ILogger<RoomService> logger)
{
    /// <summary>All rooms in list order (sort order, then name), with the item count and the total value of each.</summary>
    public async Task<List<RoomDto>> ListAsync(CancellationToken cancellationToken)
    {
        var rooms = await db.Rooms.AsNoTracking().OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
        var totals = await ItemTotals.ByRoomAsync(db, cancellationToken);
        return rooms.Select(room => ToDto(room, totals)).ToList();
    }

    public async Task<RoomDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException("Room", id);
        return ToDto(room, await ItemTotals.ByRoomAsync(db, cancellationToken));
    }

    public async Task<RoomDto> CreateAsync(CreateRoomRequest request, CancellationToken cancellationToken)
    {
        var name = CleanName(request.Name);
        await EnsureNameIsFreeAsync(name, null, cancellationToken);
        var sortOrder = request.SortOrder ?? (await db.Rooms.MaxAsync(r => (int?)r.SortOrder, cancellationToken) ?? 0) + 1;
        var now = days.Now;
        var room = new Room { Id = Guid.CreateVersion7(), Name = name, SortOrder = sortOrder, CreatedAt = now, UpdatedAt = now };
        db.Rooms.Add(room);
        db.RaiseEvent(new RoomChanged(room.Id, RoomChange.Added, room.Name, null, now));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Home: added room {RoomId}", room.Id);
        return await GetAsync(room.Id, cancellationToken);
    }

    public async Task<RoomDto> UpdateAsync(Guid id, UpdateRoomRequest request, CancellationToken cancellationToken)
    {
        var name = CleanName(request.Name);
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException("Room", id);
        await EnsureNameIsFreeAsync(name, id, cancellationToken);
        var now = days.Now;
        if (room.Name != name)
        {
            db.RaiseEvent(new RoomChanged(room.Id, RoomChange.Renamed, name, room.Name, now));
            room.Name = name;
        }
        room.SortOrder = request.SortOrder ?? room.SortOrder;
        room.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    /// <summary>Deletes a room. A room with items gets 409: move or delete the items first.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException("Room", id);
        var items = await db.Items.CountAsync(i => i.RoomId == id, cancellationToken);
        if (items > 0)
        {
            throw new ConflictException($"The room has {items} item(s). Move or delete them first.");
        }
        db.Rooms.Remove(room);
        db.RaiseEvent(new RoomChanged(room.Id, RoomChange.Deleted, room.Name, null, days.Now));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Home: deleted room {RoomId}", id);
    }

    /// <summary>A new order of all rooms: the list has each room one time. A new order raises no event.</summary>
    public async Task<List<RoomDto>> ReorderAsync(RoomOrderRequest request, CancellationToken cancellationToken)
    {
        var ids = request.RoomIds ?? throw new InvalidInputException("roomIds", "Give the ids of all rooms, in the new order.");
        if (ids.Distinct().Count() != ids.Count)
        {
            throw new InvalidInputException("roomIds", "Give each room one time.");
        }
        var rooms = await db.Rooms.ToDictionaryAsync(r => r.Id, cancellationToken);
        if (ids.Count != rooms.Count || ids.Any(id => !rooms.ContainsKey(id)))
        {
            throw new InvalidInputException("roomIds", "Give the ids of all rooms, and only of rooms that exist.");
        }
        var now = days.Now;
        for (var index = 0; index < ids.Count; index++)
        {
            var room = rooms[ids[index]];
            if (room.SortOrder != index + 1)
            {
                room.SortOrder = index + 1;
                room.UpdatedAt = now;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return await ListAsync(cancellationToken);
    }

    private static string CleanName(string? name) =>
        ItemDetails.Text(name, "name", Room.MaxNameLength) ?? throw new InvalidInputException("name", "Enter a name.");

    /// <exception cref="ConflictException">Another room has this name (any letter case).</exception>
    private async Task EnsureNameIsFreeAsync(string name, Guid? except, CancellationToken cancellationToken)
    {
        var lower = name.ToLowerInvariant();
        if (await db.Rooms.AnyAsync(r => r.Id != except && r.Name.ToLower() == lower, cancellationToken))
        {
            throw new ConflictException($"A room with the name \"{name}\" exists already.");
        }
    }

    private static RoomDto ToDto(Room room, IReadOnlyDictionary<Guid, (int Count, decimal Total)> totals)
    {
        var (count, total) = totals.TryGetValue(room.Id, out var found) ? found : (0, 0m);
        return new RoomDto(room.Id, room.Name, room.SortOrder, count, total, room.CreatedAt, room.UpdatedAt);
    }
}

/// <summary>The item counts and the total values, per room.</summary>
internal static class ItemTotals
{
    /// <summary>The count and the total value of the items in each room (the items without a room are left out).</summary>
    public static async Task<Dictionary<Guid, (int Count, decimal Total)>> ByRoomAsync(HomeDbContext db, CancellationToken cancellationToken)
    {
        var rows = await GroupsAsync(db, cancellationToken);
        return rows.Where(r => r.RoomId is not null).ToDictionary(r => r.RoomId!.Value, r => (r.Count, r.Total));
    }

    /// <summary>The count and the total value of the items per room, with one row (room null) for the items without a room.</summary>
    public static async Task<List<(Guid? RoomId, int Count, decimal Total)>> GroupsAsync(HomeDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Items.AsNoTracking()
            .GroupBy(i => i.RoomId)
            .Select(g => new
            {
                RoomId = g.Key,
                Count = g.Count(),
                Total = g.Sum(i => (i.CurrentValue ?? i.PurchasePrice ?? 0m) * i.Quantity)
            })
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.RoomId, r.Count, r.Total)).ToList();
    }
}
