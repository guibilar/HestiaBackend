namespace Hestia.Modules.Documents.Contracts;

/// <summary>
/// How other modules read documents without the Documents tables (for example Search, Timeline, or the finance
/// transaction page). The Documents module registers the implementation.
/// </summary>
public interface IDocumentQueries
{
    /// <summary>The document with its OCR text, or null when no document has this id.</summary>
    Task<DocumentDto?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The documents linked to one thing, newest first. <paramref name="kind"/> is a link kind
    /// (<see cref="DocumentLinkKinds"/>), and <paramref name="targetId"/> its id, for example a transaction id.
    /// An unknown kind gives an empty list.
    /// </summary>
    Task<IReadOnlyList<DocumentSummaryDto>> ListByLinkAsync(string kind, string targetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The documents with these ids, without the OCR text (T89): one query for many documents. Unknown ids are left out;
    /// the order is newest first.
    /// </summary>
    Task<IReadOnlyList<DocumentSummaryDto>> ListAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);
}
