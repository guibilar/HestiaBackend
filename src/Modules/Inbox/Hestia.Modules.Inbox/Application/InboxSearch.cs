using Hestia.Modules.Search.Contracts;

namespace Hestia.Modules.Inbox.Application;

/// <summary>The Inbox hits of the search (T17, T18): the open notes whose text has every word.</summary>
public sealed class InboxSearch(InboxService inbox) : ISearchSource
{
    public string Module => InboxTodayCards.ModuleName;

    public int Order => 150;

    public async Task<SearchHits> SearchAsync(string text, int limit, CancellationToken cancellationToken)
    {
        var page = await inbox.ListAsync(false, text, 1, limit, cancellationToken);
        return new SearchHits(page.Items.Select(i => new SearchHit(
            $"inbox.note.{i.Id:D}", Module, "note", Title(i.Text), null, null, null, "/inbox")).ToList(), page.TotalCount);
    }

    /// <summary>The first line of a note, at most 120 characters.</summary>
    public static string Title(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length <= 120 ? line : line[..119].TrimEnd() + "…";
    }
}
