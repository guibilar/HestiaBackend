using Hestia.Modules.Search.Contracts;

namespace Hestia.Modules.Documents.Application;

/// <summary>The Documents hits of the search (T17): the document list's full-text search (titles, tags, notes, OCR text), best first.</summary>
public sealed class DocumentSearch(DocumentService documents) : ISearchSource
{
    public string Module => "Documents";

    public int Order => 100;

    public async Task<SearchHits> SearchAsync(string text, int limit, CancellationToken cancellationToken)
    {
        var page = await documents.ListAsync(new DocumentQuery(null, null, text, null, null, 1, limit), cancellationToken);
        return new SearchHits(page.Items.Select(d => new SearchHit(
            $"documents.document.{d.Id:D}", Module, "document", d.Title, d.Kind.ToString(), d.IssuedOn, null,
            $"/documents/{d.Id:D}")).ToList(), page.TotalCount);
    }
}
