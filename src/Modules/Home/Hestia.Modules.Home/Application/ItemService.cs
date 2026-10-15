using Hestia.Modules.Home.Contracts;
using Hestia.Modules.Home.Domain;
using Hestia.Modules.Home.Infrastructure;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Paging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.Home.Application;

/// <summary>The filters of the item list and the export.</summary>
/// <param name="Room">A room id, or <c>none</c> for the items without a room, or null for all.</param>
/// <param name="Category">One category (any letter case), or null.</param>
/// <param name="Tag">One tag, or null.</param>
/// <param name="Text">A full-text search, or null.</param>
/// <param name="Warranty"><c>active</c>, <c>expiring</c>, or <c>expired</c>, or null for all.</param>
/// <param name="Page">The page, from 1.</param>
/// <param name="PageSize">The page size, 1 to 100.</param>
public sealed record ItemQuery(string? Room, string? Category, string? Tag, string? Text, string? Warranty, int Page = 1, int PageSize = 20);

public sealed class ItemService(
    HomeDbContext db,
    ItemLinkTargets linkTargets,
    ItemRelations relations,
    HomeDays days,
    ILogger<ItemService> logger)
{
    public const int MaxSearchLength = 200;
    public const int MaxLinksPerRequest = 20;

    public async Task<ItemDto> CreateAsync(CreateItemRequest request, CancellationToken cancellationToken)
    {
        var details = ItemDetails.Create(request.Name, request.RoomId, request.Category, request.Brand, request.Model,
            request.SerialNumber, request.Quantity, request.PurchasedOn, request.PurchasePrice, request.CurrentValue,
            request.WarrantyUntil, request.Notes, request.Tags);
        await EnsureRoomExistsAsync(details.RoomId, cancellationToken);
        var linkRequests = request.Links ?? [];
        if (linkRequests.Count > MaxLinksPerRequest)
        {
            throw new InvalidInputException("links", $"Add at most {MaxLinksPerRequest} links at once.");
        }
        // All links are checked before anything is saved.
        var targets = new List<(string Kind, string TargetId)>();
        foreach (var link in linkRequests)
        {
            var target = await linkTargets.CheckAsync(link?.Kind, link?.TargetId, cancellationToken);
            if (!targets.Contains(target))
            {
                targets.Add(target);
            }
        }

        var now = days.Now;
        var item = new Item { Id = Guid.CreateVersion7(), CreatedAt = now, UpdatedAt = now };
        details.ApplyTo(item);
        db.Items.Add(item);
        foreach (var (kind, targetId) in targets)
        {
            db.Links.Add(new ItemLink { Id = Guid.CreateVersion7(), ItemId = item.Id, Kind = kind, TargetId = targetId, CreatedAt = now });
        }
        db.RaiseEvent(new ItemAdded(item.Id, item.Name, item.RoomId, item.Category, item.Quantity, item.Value, item.WarrantyUntil,
            details.Tags, now));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Home: added item {ItemId}", item.Id);
        return await GetAsync(item.Id, cancellationToken);
    }

    public async Task<PaginatedList<ItemSummaryDto>> ListAsync(ItemQuery query, CancellationToken cancellationToken)
    {
        PagingExtensions.Validate(query.Page, query.PageSize);
        var ordered = Filter(query);
        var total = await ordered.CountAsync(cancellationToken);
        var items = await ItemReader.SummariesAsync(db, ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize),
            cancellationToken);
        return new PaginatedList<ItemSummaryDto> { Items = items, Page = query.Page, PageSize = query.PageSize, TotalCount = total };
    }

    /// <summary>All items that match the filters (no paging), in list order.</summary>
    public Task<List<ItemSummaryDto>> ListAllAsync(ItemQuery query, CancellationToken cancellationToken) =>
        ItemReader.SummariesAsync(db, Filter(query), cancellationToken);

    /// <summary>
    /// The items that match the filters: by name, or with a text search, best match first.
    /// </summary>
    /// <exception cref="InvalidInputException">A filter cannot be used.</exception>
    private IQueryable<Item> Filter(ItemQuery query)
    {
        IQueryable<Item> items = db.Items.AsNoTracking();

        var room = query.Room?.Trim();
        if (!string.IsNullOrEmpty(room))
        {
            if (string.Equals(room, "none", StringComparison.OrdinalIgnoreCase))
            {
                items = items.Where(i => i.RoomId == null);
            }
            else if (Guid.TryParse(room, out var roomId))
            {
                items = items.Where(i => i.RoomId == roomId);
            }
            else
            {
                throw new InvalidInputException("room", "Give a room id, or room=none for the items without a room.");
            }
        }
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var category = ItemTags.OneLine(query.Category).ToLowerInvariant();
            items = items.Where(i => i.Category != null && i.Category.ToLower() == category);
        }
        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tags = ItemTags.Normalize([query.Tag]);
            if (tags.Count != 1)
            {
                throw new InvalidInputException("tag", "Give one tag.");
            }
            var pattern = ItemTags.Pattern(tags[0]);
            items = items.Where(i => i.Tags.Contains(pattern));
        }
        items = FilterByWarranty(items, query.Warranty);

        var text = query.Text?.Trim() ?? string.Empty;
        if (text.Length > MaxSearchLength)
        {
            throw new InvalidInputException("q", $"The search text can have at most {MaxSearchLength} characters.");
        }
        if (text.Length == 0)
        {
            return items.OrderBy(i => i.Name).ThenBy(i => i.Id);
        }
        if (db.Database.IsNpgsql())
        {
            // The full-text index: web search syntax ("two words", "exact phrase", -not), in the "english" and the
            // "simple" configurations, best match first.
            return items
                .Where(i => i.SearchVector!.Matches(
                    EF.Functions.WebSearchToTsQuery("english", text).Or(EF.Functions.WebSearchToTsQuery("simple", text))))
                .OrderByDescending(i => i.SearchVector!.Rank(
                    EF.Functions.WebSearchToTsQuery("english", text).Or(EF.Functions.WebSearchToTsQuery("simple", text))))
                .ThenBy(i => i.Name)
                .ThenBy(i => i.Id);
        }
        // The in-memory test database has no full-text index: each word must be in one of the texts.
        foreach (var word in text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            items = items.Where(i =>
                i.Name.ToLower().Contains(word) || i.Tags.Contains(word) ||
                (i.Brand != null && i.Brand.ToLower().Contains(word)) ||
                (i.Model != null && i.Model.ToLower().Contains(word)) ||
                (i.SerialNumber != null && i.SerialNumber.ToLower().Contains(word)) ||
                (i.Category != null && i.Category.ToLower().Contains(word)) ||
                (i.Notes != null && i.Notes.ToLower().Contains(word)));
        }
        return items.OrderBy(i => i.Name).ThenBy(i => i.Id);
    }

    private IQueryable<Item> FilterByWarranty(IQueryable<Item> items, string? warranty)
    {
        if (string.IsNullOrWhiteSpace(warranty))
        {
            return items;
        }
        var today = days.Today;
        var soon = days.SoonUntil(today);
        return warranty.Trim().ToLowerInvariant() switch
        {
            // The last day of the warranty is today or later.
            "active" => items.Where(i => i.WarrantyUntil >= today),
            // Active, and the last day is in the next 30 days (Home:WarrantySoonDays).
            "expiring" => items.Where(i => i.WarrantyUntil >= today && i.WarrantyUntil <= soon),
            "expired" => items.Where(i => i.WarrantyUntil < today),
            _ => throw new InvalidInputException("warranty", "Use warranty=active, expiring, or expired.")
        };
    }

    public async Task<ItemDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await FindAsync(id, cancellationToken) ?? throw new NotFoundException("Item", id);

    /// <summary>The item with its documents and its maintenance tasks, or null.</summary>
    public async Task<ItemDto?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var summary = (await ItemReader.SummariesAsync(db, db.Items.AsNoTracking().Where(i => i.Id == id), cancellationToken))
            .SingleOrDefault();
        if (summary is null)
        {
            return null;
        }
        var documents = await relations.DocumentsAsync(id, summary.Links, cancellationToken);
        var tasks = await relations.TasksAsync(id, summary.Links, cancellationToken);
        return new ItemDto(summary.Id, summary.Name, summary.RoomId, summary.RoomName, summary.Category, summary.Brand, summary.Model,
            summary.SerialNumber, summary.Quantity, summary.PurchasedOn, summary.PurchasePrice, summary.CurrentValue, summary.Value,
            summary.WarrantyUntil, summary.Notes, summary.Tags, summary.CreatedAt, summary.UpdatedAt, summary.Links, documents, tasks);
    }

    public async Task<ItemDto> UpdateAsync(Guid id, UpdateItemRequest request, CancellationToken cancellationToken)
    {
        var details = ItemDetails.Create(request.Name, request.RoomId, request.Category, request.Brand, request.Model,
            request.SerialNumber, request.Quantity, request.PurchasedOn, request.PurchasePrice, request.CurrentValue,
            request.WarrantyUntil, request.Notes, request.Tags);
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new NotFoundException("Item", id);
        await EnsureRoomExistsAsync(details.RoomId, cancellationToken);
        var now = days.Now;
        details.ApplyTo(item);
        item.UpdatedAt = now;
        db.RaiseEvent(Changed(item, ItemChange.Details, now));
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.Items.Include(i => i.Links).FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new NotFoundException("Item", id);
        db.RaiseEvent(new ItemDeleted(item.Id, item.Name, days.Now));
        // The links go with the item (cascade). Documents and Tasks remove their links to it on ItemDeleted.
        db.Items.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Home: deleted item {ItemId}", id);
    }

    public async Task<ItemLinkDto> AddLinkAsync(Guid id, CreateItemLinkRequest request, CancellationToken cancellationToken)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new NotFoundException("Item", id);
        var (kind, targetId) = await linkTargets.CheckAsync(request.Kind, request.TargetId, cancellationToken);
        if (await db.Links.AnyAsync(l => l.ItemId == id && l.Kind == kind && l.TargetId == targetId, cancellationToken))
        {
            throw new ConflictException("The item has this link already.");
        }

        var now = days.Now;
        var link = new ItemLink { Id = Guid.CreateVersion7(), ItemId = id, Kind = kind, TargetId = targetId, CreatedAt = now };
        db.Links.Add(link);
        item.UpdatedAt = now;
        db.RaiseEvent(Changed(item, ItemChange.Links, now));
        await db.SaveChangesAsync(cancellationToken);
        return new ItemLinkDto(link.Id, link.Kind, link.TargetId, link.CreatedAt);
    }

    public async Task RemoveLinkAsync(Guid id, Guid linkId, CancellationToken cancellationToken)
    {
        var link = await db.Links.FirstOrDefaultAsync(l => l.Id == linkId && l.ItemId == id, cancellationToken)
            ?? throw new NotFoundException("Link", linkId);
        var item = await db.Items.FirstAsync(i => i.Id == id, cancellationToken);
        var now = days.Now;
        db.Links.Remove(link);
        item.UpdatedAt = now;
        db.RaiseEvent(Changed(item, ItemChange.Links, now));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The items of one thing, by name: the items that link to it, and (for a document or a task) the items that it links
    /// to (kind <c>Home.Item</c>).
    /// </summary>
    public async Task<List<ItemSummaryDto>> ListByLinkAsync(string? kind, string? targetId, CancellationToken cancellationToken)
    {
        var (linkKind, target) = ItemLinkTargets.Normalize(kind, targetId);
        var linkedFrom = await relations.ItemsLinkedFromAsync(linkKind, Guid.Parse(target), cancellationToken);
        var items = db.Items.AsNoTracking()
            .Where(i => i.Links.Any(l => l.Kind == linkKind && l.TargetId == target) || linkedFrom.Contains(i.Id))
            .OrderBy(i => i.Name)
            .ThenBy(i => i.Id);
        return await ItemReader.SummariesAsync(db, items, cancellationToken);
    }

    /// <summary>The categories in use (T89), by name, with the count of items in each.</summary>
    public async Task<List<CategoryCountDto>> CategoriesAsync(CancellationToken cancellationToken) =>
        (await db.Items.AsNoTracking().Where(i => i.Category != null).Select(i => i.Category!).ToListAsync(cancellationToken))
            .GroupBy(category => category, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CategoryCountDto(group.First(), group.Count()))
            .OrderBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// All maintenance (T89): the open tasks linked to an item from either side, by due date (no date last), then by
    /// title and item name.
    /// </summary>
    public async Task<List<MaintenanceDto>> MaintenanceAsync(CancellationToken cancellationToken)
    {
        var itemLinks = (await db.Links.AsNoTracking()
                .Where(l => l.Kind == ItemLinkKinds.TasksTask)
                .Select(l => new { l.ItemId, l.TargetId })
                .ToListAsync(cancellationToken))
            .Select(l => (l.ItemId, l.TargetId))
            .ToList();
        var pairs = await relations.MaintenanceAsync(itemLinks, cancellationToken);
        var itemIds = pairs.Select(p => p.ItemId).ToHashSet();
        var items = await db.Items.AsNoTracking()
            .Where(i => itemIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Name, Room = i.RoomId == null ? null : db.Rooms.Where(r => r.Id == i.RoomId).Select(r => r.Name).FirstOrDefault() })
            .ToDictionaryAsync(i => i.Id, cancellationToken);
        return pairs
            .Where(p => items.ContainsKey(p.ItemId))
            .Select(p => new MaintenanceDto(p.Task.Id, p.Task.Title, p.Task.DueOn, p.Task.Repeat?.Every, p.Task.Repeat?.Unit.ToString(),
                p.Task.LastDoneAt, p.ItemId, items[p.ItemId].Name, items[p.ItemId].Room))
            .OrderBy(m => m.DueOn is null)
            .ThenBy(m => m.DueOn)
            .ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.TaskId)
            .ToList();
    }

    /// <summary>The numbers of the Home screen: the item count, the total value, the rooms, and the warranties that end soon.</summary>
    public async Task<HomeSummaryDto> SummaryAsync(CancellationToken cancellationToken)
    {
        var today = days.Today;
        var soon = days.SoonUntil(today);
        var groups = await ItemTotals.GroupsAsync(db, cancellationToken);
        var rooms = await db.Rooms.AsNoTracking().OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ThenBy(r => r.Id)
            .Select(r => new { r.Id, r.Name })
            .ToListAsync(cancellationToken);
        var counts = new List<RoomCountDto>();
        foreach (var room in rooms)
        {
            var found = groups.Where(g => g.RoomId == room.Id).ToList();
            counts.Add(new RoomCountDto(room.Id, room.Name, found.Sum(g => g.Count), found.Sum(g => g.Total)));
        }
        var withoutRoom = groups.Where(g => g.RoomId is null).ToList();
        if (withoutRoom.Count > 0)
        {
            counts.Add(new RoomCountDto(null, null, withoutRoom.Sum(g => g.Count), withoutRoom.Sum(g => g.Total)));
        }
        var ending = await db.Items.CountAsync(i => i.WarrantyUntil >= today && i.WarrantyUntil <= soon, cancellationToken);
        return new HomeSummaryDto(today, groups.Sum(g => g.Count), groups.Sum(g => g.Total), counts, ending);
    }

    /// <exception cref="InvalidInputException">No room has this id.</exception>
    private async Task EnsureRoomExistsAsync(Guid? roomId, CancellationToken cancellationToken)
    {
        if (roomId is { } id && !await db.Rooms.AnyAsync(r => r.Id == id, cancellationToken))
        {
            throw new InvalidInputException("roomId", "No room has this id.");
        }
    }

    private static ItemChanged Changed(Item item, ItemChange change, DateTimeOffset now) =>
        new(item.Id, change, item.Name, item.RoomId, item.Category, item.Quantity, item.Value, item.WarrantyUntil,
            ItemTags.Read(item.Tags), now);
}

/// <summary>Reads items as DTOs, with the name of the room and the links.</summary>
internal static class ItemReader
{
    /// <summary>Reads the items of an (ordered) query.</summary>
    public static async Task<List<ItemSummaryDto>> SummariesAsync(HomeDbContext db, IQueryable<Item> items, CancellationToken cancellationToken)
    {
        var rows = await items
            .Select(i => new
            {
                Item = i,
                RoomName = db.Rooms.Where(r => r.Id == i.RoomId).Select(r => r.Name).FirstOrDefault(),
                Links = i.Links.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
                    .Select(l => new ItemLinkDto(l.Id, l.Kind, l.TargetId, l.CreatedAt)).ToList()
            })
            .ToListAsync(cancellationToken);
        return rows.Select(row => Summary(row.Item, row.RoomName, row.Links)).ToList();
    }

    public static ItemSummaryDto Summary(Item item, string? roomName, IReadOnlyList<ItemLinkDto> links) =>
        new(item.Id, item.Name, item.RoomId, roomName, item.Category, item.Brand, item.Model, item.SerialNumber, item.Quantity,
            item.PurchasedOn, item.PurchasePrice, item.CurrentValue, item.Value, item.WarrantyUntil, item.Notes,
            ItemTags.Read(item.Tags), item.CreatedAt, item.UpdatedAt, links);
}

/// <summary>The other modules' queries (<see cref="IHomeQueries"/>). They read only the Home tables (no loop with the
/// modules that Home calls).</summary>
public sealed class HomeQueries(HomeDbContext db) : IHomeQueries
{
    public async Task<ItemSummaryDto?> FindItemAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await ItemReader.SummariesAsync(db, db.Items.AsNoTracking().Where(i => i.Id == id), cancellationToken)).SingleOrDefault();

    public async Task<IReadOnlyList<ItemSummaryDto>> ListByLinkAsync(string kind, string targetId, CancellationToken cancellationToken = default)
    {
        string linkKind, target;
        try
        {
            (linkKind, target) = ItemLinkTargets.Normalize(kind, targetId);
        }
        catch (InvalidInputException)
        {
            return [];
        }
        var items = db.Items.AsNoTracking()
            .Where(i => i.Links.Any(l => l.Kind == linkKind && l.TargetId == target))
            .OrderBy(i => i.Name)
            .ThenBy(i => i.Id);
        return await ItemReader.SummariesAsync(db, items, cancellationToken);
    }
}
