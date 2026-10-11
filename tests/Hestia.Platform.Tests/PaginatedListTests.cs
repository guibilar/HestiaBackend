using Hestia.Platform.Exceptions;
using Hestia.Platform.Paging;

namespace Hestia.Platform.Tests;

public class PaginatedListTests
{
    [Theory]
    [InlineData(1, 10, 0, 0, false, false)]
    [InlineData(1, 10, 10, 1, false, false)]
    [InlineData(1, 10, 11, 2, false, true)]
    [InlineData(2, 10, 11, 2, true, false)]
    [InlineData(1, 0, 5, 0, false, false)]
    public void PageFigures(int page, int pageSize, int totalCount, int totalPages, bool hasPrevious, bool hasNext)
    {
        var list = new PaginatedList<string> { Page = page, PageSize = pageSize, TotalCount = totalCount };

        Assert.Equal(totalPages, list.TotalPages);
        Assert.Equal(hasPrevious, list.HasPreviousPage);
        Assert.Equal(hasNext, list.HasNextPage);
    }

    [Theory]
    [InlineData(0, 10, "page")]
    [InlineData(1, 0, "pageSize")]
    [InlineData(1, PagingExtensions.MaxPageSize + 1, "pageSize")]
    public void Validate_RejectsAPageOutOfRange(int page, int pageSize, string field)
    {
        var exception = Assert.Throws<InvalidInputException>(() => PagingExtensions.Validate(page, pageSize));

        Assert.Equal(field, exception.Field);
    }

    [Fact]
    public void Validate_AcceptsTheLimits()
    {
        PagingExtensions.Validate(1, 1);
        PagingExtensions.Validate(1, PagingExtensions.MaxPageSize);
    }
}
