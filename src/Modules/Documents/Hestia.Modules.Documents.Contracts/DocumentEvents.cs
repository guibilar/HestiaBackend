using Hestia.Contracts;

namespace Hestia.Modules.Documents.Contracts;

// The events of the Documents module (D7). The module uses durable events (T38): each event is stored in the outbox
// of the "documents" schema with the change, and the host delivers it after the commit, also after a restart.
// An event can come more than once: make each consumer idempotent with the EventId (T36, the module event inbox).
// To read more than an event carries (for example the OCR text), use IDocumentQueries.

/// <summary>A document was uploaded.</summary>
/// <param name="DocumentId">The document.</param>
/// <param name="Title">The title.</param>
/// <param name="Kind">What the document is.</param>
/// <param name="Tags">The tags.</param>
/// <param name="IssuedOn">The day the document was issued, if known.</param>
/// <param name="ExpiresOn">The day the document expires, if it does.</param>
/// <param name="FileCount">How many files the document has.</param>
/// <param name="OccurredAt">When the document was added.</param>
public sealed record DocumentAdded(
    Guid DocumentId,
    string Title,
    DocumentKind Kind,
    IReadOnlyList<string> Tags,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    int FileCount,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>What changed in a document.</summary>
public enum DocumentChange
{
    /// <summary>The user changed the details: the title, the kind, the tags, the dates, or the notes.</summary>
    Details,

    /// <summary>The OCR finished (done or failed), so the OCR text changed. Read it with IDocumentQueries.</summary>
    OcrText
}

/// <summary>A document changed. The values are the ones after the change.</summary>
public sealed record DocumentChanged(
    Guid DocumentId,
    DocumentChange Change,
    string Title,
    DocumentKind Kind,
    IReadOnlyList<string> Tags,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    OcrStatus OcrStatus,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>
/// A document was deleted, with its files. Before this event, the module sends <see cref="DocumentUnlinked"/> for each
/// of its links.
/// </summary>
public sealed record DocumentDeleted(Guid DocumentId, string Title, DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>A document got a link, for example to a finance transaction.</summary>
/// <param name="DocumentId">The document.</param>
/// <param name="LinkId">The link.</param>
/// <param name="LinkKind">What the link points to, for example <c>Money.Transaction</c>.</param>
/// <param name="TargetId">The id of the thing, as text.</param>
/// <param name="OccurredAt">When the link was made.</param>
public sealed record DocumentLinked(Guid DocumentId, Guid LinkId, string LinkKind, string TargetId, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>A link of a document was removed (by the user, or because the document was deleted).</summary>
public sealed record DocumentUnlinked(Guid DocumentId, Guid LinkId, string LinkKind, string TargetId, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}
