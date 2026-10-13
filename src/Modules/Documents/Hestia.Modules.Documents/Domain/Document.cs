using Hestia.Modules.Documents.Contracts;
using Hestia.Platform.Domain;
using NpgsqlTypes;

namespace Hestia.Modules.Documents.Domain;

/// <summary>A document: a receipt, a warranty, a lease, and so on. It has one or more files.</summary>
public class Document : BaseEntity
{
    public const int MaxTitleLength = 200;
    public const int MaxNotesLength = 4000;
    public const int MaxOcrErrorLength = 2000;

    /// <summary>The OCR text is cut at this length, so the full-text index of one document stays small.</summary>
    public const int MaxOcrTextLength = 200_000;

    public string Title { get; set; } = string.Empty;
    public DocumentKind Kind { get; set; }

    /// <summary>The tags, stored as <c>|tag1|tag2|</c> (see <see cref="DocumentTags"/>), or empty.</summary>
    public string Tags { get; set; } = string.Empty;

    public DateOnly? IssuedOn { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public OcrStatus OcrStatus { get; set; }
    public string? OcrText { get; set; }
    public string? OcrError { get; set; }

    /// <summary>
    /// The full-text index of the title, the tags, the notes, and the OCR text: a generated column (PostgreSQL only).
    /// The database computes it; the code never sets it.
    /// </summary>
    public NpgsqlTsVector? SearchVector { get; set; }

    public List<DocumentFile> Files { get; set; } = [];
    public List<DocumentLink> Links { get; set; } = [];
}

/// <summary>
/// One file of a document. The content is on the documents volume, under <see cref="StorageKey"/>. The key comes from the
/// SHA-256 of the content, so the same content is stored once, also for two documents.
/// </summary>
public class DocumentFile : BaseEntity
{
    public const int MaxFileNameLength = 255;
    public const int MaxContentTypeLength = 100;
    public const int Sha256Length = 64;
    public const int StorageKeyLength = 67;

    public Guid DocumentId { get; set; }

    /// <summary>The place of the file in its document, from 0.</summary>
    public int Position { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>
/// A link from a document to something outside the module. The kind and the id are text, so later modules (Home
/// inventory items, People) can use the same table.
/// </summary>
public class DocumentLink : BaseEntity
{
    public const int MaxKindLength = 50;
    public const int MaxTargetIdLength = 100;

    public Guid DocumentId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
