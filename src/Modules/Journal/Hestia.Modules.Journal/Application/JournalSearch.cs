using Hestia.Modules.Search.Contracts;

namespace Hestia.Modules.Journal.Application;

/// <summary>The Journal hits of the search (T17): the entries whose text matches (full-text search), best first.</summary>
public sealed class JournalSearch(JournalService journal) : ISearchSource
{
    public string Module => JournalTodayCards.ModuleName;

    public int Order => 140;

    public async Task<SearchHits> SearchAsync(string text, int limit, CancellationToken cancellationToken)
    {
        var page = await journal.ListAsync(new JournalQuery(text, null, null, 1, limit), cancellationToken);
        return new SearchHits(page.Items.Select(e => new SearchHit(
            $"journal.entry.{e.Id:D}", Module, "entry", "Journal entry", e.Excerpt, e.Day, null,
            $"/journal?day={e.Day:yyyy-MM-dd}")).ToList(), page.TotalCount);
    }
}
