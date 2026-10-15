namespace Hestia.Modules.Documents.Contracts;

/// <summary>What a document is. Clients pick the icon and the filter with it.</summary>
public enum DocumentKind
{
    Receipt,
    Warranty,
    Lease,
    Tax,
    Insurance,
    Manual,
    Other
}

/// <summary>The state of the text recognition (OCR) of a document.</summary>
public enum OcrStatus
{
    /// <summary>The OCR did not run yet. It runs in the background, soon after the upload.</summary>
    Pending,

    /// <summary>The OCR ran on all files. <c>OcrText</c> has the text (or nothing, when the files have no text).</summary>
    Done,

    /// <summary>The OCR failed on one or more files. <c>OcrError</c> says why. A retry runs it again.</summary>
    Failed,

    /// <summary>No OCR: it was off (<c>Documents:Ocr:Enabled</c>) when the document was added.</summary>
    NotNeeded
}

/// <summary>One file of a document. Download it at <c>/api/documents/{documentId}/files/{id}</c>.</summary>
/// <param name="Id">The file.</param>
/// <param name="FileName">The name of the file when it was uploaded.</param>
/// <param name="ContentType">The type that the host found in the file content (not the type that the client sent).</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="Sha256">The SHA-256 hash of the content, in lower-case hexadecimal.</param>
/// <param name="AddedAt">When the file was uploaded.</param>
public sealed record DocumentFileDto(Guid Id, string FileName, string ContentType, long Size, string Sha256, DateTimeOffset AddedAt);

/// <summary>A link from a document to something outside the Documents module, for example a finance transaction.</summary>
/// <param name="Id">The link.</param>
/// <param name="Kind">What the link points to, for example <c>Money.Transaction</c> (<see cref="DocumentLinkKinds"/>).</param>
/// <param name="TargetId">The id of the thing, as text.</param>
/// <param name="CreatedAt">When the link was made.</param>
public sealed record DocumentLinkDto(Guid Id, string Kind, string TargetId, DateTimeOffset CreatedAt);

/// <summary>A document in a list: everything except the OCR text.</summary>
public sealed record DocumentSummaryDto(
    Guid Id,
    string Title,
    DocumentKind Kind,
    IReadOnlyList<string> Tags,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    OcrStatus OcrStatus,
    IReadOnlyList<DocumentFileDto> Files,
    IReadOnlyList<DocumentLinkDto> Links);

/// <summary>One document, with its OCR text.</summary>
/// <param name="Id">The document.</param>
/// <param name="Title">The title.</param>
/// <param name="Kind">What the document is.</param>
/// <param name="Tags">The tags, in lower case.</param>
/// <param name="IssuedOn">The day the document was issued, if known.</param>
/// <param name="ExpiresOn">The day the document expires (a warranty, a lease, a policy), if it does.</param>
/// <param name="Notes">Free text.</param>
/// <param name="CreatedAt">When the document was added.</param>
/// <param name="UpdatedAt">When the user last changed its details.</param>
/// <param name="OcrStatus">The state of the text recognition.</param>
/// <param name="OcrError">Why the OCR failed, when it failed.</param>
/// <param name="OcrText">The text of the files (OCR, or the text layer of a PDF). Null before the OCR.</param>
/// <param name="Files">The files, in upload order.</param>
/// <param name="Links">The links, oldest first.</param>
public sealed record DocumentDto(
    Guid Id,
    string Title,
    DocumentKind Kind,
    IReadOnlyList<string> Tags,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    OcrStatus OcrStatus,
    string? OcrError,
    string? OcrText,
    IReadOnlyList<DocumentFileDto> Files,
    IReadOnlyList<DocumentLinkDto> Links);

/// <summary>
/// The new details of a document (<c>PUT /api/documents/{id}</c>). All values replace the old ones: a value that is not
/// sent becomes empty. The files do not change.
/// </summary>
public sealed record UpdateDocumentRequest(
    string? Title,
    DocumentKind? Kind,
    IReadOnlyList<string>? Tags,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    string? Notes);

/// <summary>
/// How many documents link to each of some things (<c>POST /api/documents/by-link/counts</c>, T72), for example the
/// transactions of a page: an icon on the rows that have documents.
/// </summary>
/// <param name="Kind">A link kind, for example <c>Money.Transaction</c>.</param>
/// <param name="TargetIds">The ids of the things (at most 200).</param>
public sealed record DocumentLinkCountsRequest(string? Kind, IReadOnlyList<string>? TargetIds);

/// <summary>A new link (<c>POST /api/documents/{id}/links</c>).</summary>
/// <param name="Kind">What to link to: <c>Money.Transaction</c>, <c>Home.Item</c>, or <c>People.Person</c>.</param>
/// <param name="TargetId">The id of the thing, for example the transaction id.</param>
public sealed record CreateDocumentLinkRequest(string? Kind, string? TargetId);

/// <summary>The kinds of link that the Documents module knows.</summary>
public static class DocumentLinkKinds
{
    /// <summary>A finance transaction. The target id is the transaction id (a GUID).</summary>
    public const string MoneyTransaction = "Money.Transaction";

    /// <summary>An item of the Home module (T22), for example the receipt or the manual of a dishwasher. The target id is the item id.</summary>
    public const string HomeItem = "Home.Item";

    /// <summary>A person of the People module (T93), for example the doctor of a prescription. The target id is the person id.</summary>
    public const string PeoplePerson = "People.Person";

    public static readonly IReadOnlyList<string> All = [MoneyTransaction, HomeItem, PeoplePerson];
}
