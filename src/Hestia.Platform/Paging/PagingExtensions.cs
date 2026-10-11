using Hestia.Platform.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Platform.Paging;

public static class PagingExtensions
{
    public const int MaxPageSize = 100;

    /// <summary>Reads one page of an ordered query. A page number or size out of range is a 400.</summary>
    public static async Task<PaginatedList<T>> ToPaginatedListAsync<T>(
        this IQueryable<T> query, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        Validate(page, pageSize);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PaginatedList<T> { Items = items, Page = page, PageSize = pageSize, TotalCount = totalCount };
    }

    public static void Validate(int page, int pageSize)
    {
        if (page < 1)
        {
            throw new InvalidInputException("page", "The page number must be 1 or more.");
        }
        if (pageSize is < 1 or > MaxPageSize)
        {
            throw new InvalidInputException("pageSize", $"The page size must be from 1 to {MaxPageSize}.");
        }
    }
}
