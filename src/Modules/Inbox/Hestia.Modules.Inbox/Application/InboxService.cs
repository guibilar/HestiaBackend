using Hestia.Modules.Inbox.Contracts;
using Hestia.Modules.Inbox.Domain;
using Hestia.Modules.Inbox.Infrastructure;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Paging;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.Inbox.Application;

/// <summary>
/// The Inbox (T18): notes kept to sort later. The clients sort a note into another module themselves (they add the task,
/// or write the journal), then mark it done here with where it went.
/// </summary>
public sealed class InboxService(InboxDbContext db, TimeProvider time)
{
    /// <summary>The open notes, newest first; with <paramref name="done"/>, the sorted ones, last sorted first.</summary>
    public async Task<PaginatedList<InboxItemDto>> ListAsync(bool done, string? text, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        PagingExtensions.Validate(page, pageSize);
        var items = db.Items.AsNoTracking().Where(i => (i.DoneAt != null) == done);
        foreach (var word in (text ?? string.Empty).Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            items = items.Where(i => i.Text.ToLower().Contains(word));
        }
        var ordered = done
            ? items.OrderByDescending(i => i.DoneAt).ThenByDescending(i => i.Id)
            : items.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id);
        var total = await items.CountAsync(cancellationToken);
        var list = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PaginatedList<InboxItemDto> { Items = list.Select(Dto).ToList(), Page = page, PageSize = pageSize, TotalCount = total };
    }

    public Task<int> OpenCountAsync(CancellationToken cancellationToken) =>
        db.Items.CountAsync(i => i.DoneAt == null, cancellationToken);

    public async Task<InboxItemDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Dto(await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new NotFoundException("Inbox note", id));

    public async Task<InboxItemDto> AddAsync(InboxTextRequest request, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var item = new InboxItem { Text = Text(request.Text), CreatedAt = now, UpdatedAt = now };
        db.Items.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return Dto(item);
    }

    public async Task<InboxItemDto> UpdateAsync(Guid id, InboxTextRequest request, CancellationToken cancellationToken)
    {
        var item = await FindAsync(id, cancellationToken);
        item.Text = Text(request.Text);
        item.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Dto(item);
    }

    /// <summary>Marks the note sorted. A note that is sorted already keeps its first time and place.</summary>
    public async Task<InboxItemDto> DoneAsync(Guid id, InboxDoneRequest request, CancellationToken cancellationToken)
    {
        var sortedTo = string.IsNullOrWhiteSpace(request.SortedTo) ? null : request.SortedTo.Trim();
        if (sortedTo?.Length > InboxLimits.MaxSortedToLength)
        {
            throw new InvalidInputException("sortedTo", $"sortedTo can have at most {InboxLimits.MaxSortedToLength} characters.");
        }
        var item = await FindAsync(id, cancellationToken);
        if (item.DoneAt is null)
        {
            item.DoneAt = time.GetUtcNow();
            item.SortedTo = sortedTo;
            item.UpdatedAt = item.DoneAt.Value;
            await db.SaveChangesAsync(cancellationToken);
        }
        return Dto(item);
    }

    /// <summary>Back to the open notes (an undo).</summary>
    public async Task<InboxItemDto> ReopenAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await FindAsync(id, cancellationToken);
        if (item.DoneAt is not null)
        {
            item.DoneAt = null;
            item.SortedTo = null;
            item.UpdatedAt = time.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
        return Dto(item);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        db.Items.Remove(await FindAsync(id, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<InboxItem> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Items.FirstOrDefaultAsync(i => i.Id == id, cancellationToken) ?? throw new NotFoundException("Inbox note", id);

    private static string Text(string? text)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            throw new InvalidInputException("text", "Write a note.");
        }
        if (value.Length > InboxLimits.MaxTextLength)
        {
            throw new InvalidInputException("text", $"A note can have at most {InboxLimits.MaxTextLength} characters.");
        }
        return value;
    }

    public static InboxItemDto Dto(InboxItem item) => new(item.Id, item.Text, item.CreatedAt, item.UpdatedAt, item.DoneAt, item.SortedTo,
        item.Source, item.From, DocumentIdList.Read(item.DocumentIds));
}

/// <summary>The documents of an email note, as stored in <see cref="InboxItem.DocumentIds"/>.</summary>
public static class DocumentIdList
{
    public static IReadOnlyList<Guid> Read(string stored) =>
        stored.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(text => Guid.TryParse(text, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).ToList();

    public static string Write(IReadOnlyCollection<Guid> ids) =>
        ids.Count == 0 ? string.Empty : "|" + string.Join("|", ids.Select(id => id.ToString("D"))) + "|";
}
