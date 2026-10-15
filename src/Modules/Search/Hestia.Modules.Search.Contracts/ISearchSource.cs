namespace Hestia.Modules.Search.Contracts;

/// <summary>
/// The search of one module (T17): its own things that match a text. A module that can be searched implements it, and
/// registers it in its <c>AddServices</c> as a scoped service: <c>services.AddScoped&lt;ISearchSource, DocumentSearch&gt;()</c>.
/// The Search module calls all sources in parallel for each <c>GET /api/search</c>, inside the HTTP request of the user.
/// </summary>
/// <remarks>
/// Each module searches its own tables in its own way (D15: PostgreSQL full-text search where the module has an index,
/// for example Documents and Journal), best match first. The Search module keeps no index. A source that throws, or does
/// not answer in time (<c>Search:SourceTimeout</c>), gives no hits; the other modules still show theirs.
/// </remarks>
public interface ISearchSource
{
    /// <summary>The module name, for example <c>Documents</c>. The same in every hit.</summary>
    string Module { get; }

    /// <summary>The place of the module in the answer: lower first. Money is 0; other modules use 100 or more.</summary>
    int Order { get; }

    /// <summary>At most <paramref name="limit"/> hits for <paramref name="text"/> (trimmed, 2 to 200 characters), best first.</summary>
    Task<SearchHits> SearchAsync(string text, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// One thing that matches: a document, a task, a transaction. The clients show <see cref="Title"/> and
/// <see cref="Text"/> as they are, and format <see cref="Amount"/> as money and <see cref="Day"/> as a date.
/// </summary>
/// <param name="Id">Stable and unique: the lower-case module name, a dot, the kind, a dot, and the id.</param>
/// <param name="Module">The module, for example <c>Documents</c>.</param>
/// <param name="Kind">What it is inside the module, for example <c>document</c>. The clients pick an icon by it.</param>
/// <param name="Title">One line, for example the title of the document.</param>
/// <param name="Text">A second line, or null.</param>
/// <param name="Day">A day that tells about it (the transaction's day, the due day), or null.</param>
/// <param name="Amount">An amount of money (money in is positive), or null.</param>
/// <param name="Link">The web route of the thing; the Android app maps it.</param>
public sealed record SearchHit(
    string Id,
    string Module,
    string Kind,
    string Title,
    string? Text,
    DateOnly? Day,
    decimal? Amount,
    string? Link);

/// <param name="Hits">The hits, best first.</param>
/// <param name="Total">All matches of the module, when it knows them (can be more than the hits).</param>
public sealed record SearchHits(IReadOnlyList<SearchHit> Hits, int Total);
