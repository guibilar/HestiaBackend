using Hestia.Modules.Sample.Contracts;
using Hestia.Modules.Sample.Domain;
using Hestia.Modules.Sample.Infrastructure;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Paging;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.Sample.Application;

public class SampleItemService(SampleDbContext db, TimeProvider time)
{
    public Task<PaginatedList<SampleItemDto>> ListAsync(int page, int pageSize, CancellationToken cancellationToken) =>
        db.Items.AsNoTracking()
            .OrderBy(item => item.Name)
            .Select(item => new SampleItemDto(item.Id, item.Name, item.CreatedAt))
            .ToPaginatedListAsync(page, pageSize, cancellationToken);

    public async Task<SampleItemDto> CreateAsync(CreateSampleItemRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw new InvalidInputException("name", "Enter a name.");
        }
        if (name.Length > SampleItem.MaxNameLength)
        {
            throw new InvalidInputException("name", $"The name can have at most {SampleItem.MaxNameLength} characters.");
        }
        if (await db.Items.AnyAsync(item => item.Name == name, cancellationToken))
        {
            throw new ConflictException($"An item with the name \"{name}\" exists already.");
        }

        var item = new SampleItem { Id = Guid.CreateVersion7(), Name = name, CreatedAt = time.GetUtcNow() };
        db.Items.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item);
    }

    internal static SampleItemDto ToDto(SampleItem item) => new(item.Id, item.Name, item.CreatedAt);
}

public class SampleItemQueries(SampleDbContext db) : ISampleItemQueries
{
    public async Task<SampleItemDto?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        return item is null ? null : SampleItemService.ToDto(item);
    }
}
