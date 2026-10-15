using Hestia.Modules.Search.Contracts;

namespace Hestia.Modules.Home.Application;

/// <summary>The Home hits of the search (T17): items whose name, brand, model, category, serial number, or notes have the text.</summary>
public sealed class HomeSearch(ItemService items) : ISearchSource
{
    public string Module => "Home";

    public int Order => 120;

    public async Task<SearchHits> SearchAsync(string text, int limit, CancellationToken cancellationToken)
    {
        var page = await items.ListAsync(new ItemQuery(null, null, null, text, null, 1, limit), cancellationToken);
        return new SearchHits(page.Items.Select(i => new SearchHit(
            $"home.item.{i.Id:D}", Module, "item", i.Name, i.RoomName ?? i.Category, i.PurchasedOn, null,
            $"/home/items/{i.Id:D}")).ToList(), page.TotalCount);
    }
}
