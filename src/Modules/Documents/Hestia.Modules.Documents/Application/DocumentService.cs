using System.Linq.Expressions;
using Hestia.Modules.Documents.Application.Ocr;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Documents.Domain;
using Hestia.Modules.Documents.Infrastructure;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Paging;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Documents.Application;

/// <summary>An upload: the details as text (from the form) and the files.</summary>
public sealed record DocumentUpload(
    string? Title,
    string? Kind,
    IReadOnlyList<string> Tags,
    string? IssuedOn,
    string? ExpiresOn,
    string? Notes,
    IReadOnlyList<IFormFile> Files);

/// <summary>The filters of the document list.</summary>
public sealed record DocumentQuery(
    string? Kind,
    string? Tag,
    string? Text,
    string? ExpiresFrom,
    string? ExpiresTo,
    int Page,
    int PageSize,
    string? Expiring = null);

/// <summary>A file to send to the client.</summary>
public sealed record DocumentFileContent(string Path, string ContentType, string FileName);

public sealed class DocumentService(
    DocumentsDbContext db,
    DocumentStorage storage,
    LinkTargets linkTargets,
    OcrSignal ocrSignal,
    IOptions<DocumentsOptions> options,
    TimeProvider time,
    ILogger<DocumentService> logger)
{
    public const int MaxSearchLength = 200;

    private DocumentsOptions Settings => options.Value;

    public async Task<DocumentDto> UploadAsync(DocumentUpload upload, CancellationToken cancellationToken)
    {
        if (upload.Files.Count == 0)
        {
            throw new InvalidInputException("files", "Add at least one file.");
        }
        if (upload.Files.Count > Settings.MaxFilesPerDocument)
        {
            throw new InvalidInputException("files", $"A document can have at most {Settings.MaxFilesPerDocument} files.");
        }
        // Without a title, the name of the first file (without its extension) is the title.
        var title = string.IsNullOrWhiteSpace(upload.Title)
            ? Path.GetFileNameWithoutExtension(CleanFileName(upload.Files[0].FileName, null))
            : upload.Title;
        var details = DocumentDetails.Create(title, DocumentDetails.ParseKind(upload.Kind, "kind") ?? DocumentKind.Other,
            upload.Tags, DocumentDetails.ParseDay(upload.IssuedOn, "issuedOn"), DocumentDetails.ParseDay(upload.ExpiresOn, "expiresOn"),
            upload.Notes);

        var now = time.GetUtcNow();
        var ocr = Settings.Ocr.Enabled ? OcrStatus.Pending : OcrStatus.NotNeeded;
        var document = new Document
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = now,
            UpdatedAt = now,
            OcrStatus = ocr
        };
        details.ApplyTo(document);

        var stored = new List<StoredFile>();
        try
        {
            foreach (var file in upload.Files)
            {
                var content = await StoreAsync(file, cancellationToken);
                stored.Add(content);
                document.Files.Add(new DocumentFile
                {
                    Id = Guid.CreateVersion7(),
                    DocumentId = document.Id,
                    Position = document.Files.Count,
                    FileName = CleanFileName(file.FileName, content.ContentType),
                    ContentType = content.ContentType!,
                    Size = content.Size,
                    Sha256 = content.Sha256,
                    StorageKey = content.Key,
                    AddedAt = now
                });
            }

            db.Documents.Add(document);
            db.RaiseEvent(new DocumentAdded(document.Id, document.Title, document.Kind, details.Tags, document.IssuedOn,
                document.ExpiresOn, document.Files.Count, now));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Nothing is saved: remove the files that this upload wrote.
            await RemoveUnusedAsync(stored.Where(file => file.Created).Select(file => file.Key));
            throw;
        }

        if (ocr == OcrStatus.Pending)
        {
            ocrSignal.Wake();
        }
        logger.LogInformation("Documents: added document {DocumentId} with {Files} file(s)", document.Id, document.Files.Count);
        return (await FindAsync(document.Id, cancellationToken))!;
    }

    private async Task<StoredFile> StoreAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var name = CleanFileName(file.FileName, null);
        if (file.Length > Settings.MaxFileSize)
        {
            throw TooLarge(name);
        }
        StoredFile stored;
        await using (var stream = file.OpenReadStream())
        {
            try
            {
                stored = await storage.StoreAsync(stream, Settings.MaxFileSize, cancellationToken);
            }
            catch (FileTooLargeException)
            {
                throw TooLarge(name);
            }
        }
        if (stored.ContentType is null || !Settings.ContentTypes.Contains(stored.ContentType))
        {
            if (stored.Created)
            {
                await RemoveUnusedAsync([stored.Key]);
            }
            var allowed = string.Join(", ", Settings.ContentTypes.Select(FileTypes.Name));
            throw new InvalidInputException("files", $"\"{name}\" is not a file type that a document can have ({allowed}).");
        }
        return stored;
    }

    private InvalidInputException TooLarge(string name) =>
        new("files", $"\"{name}\" is larger than {Settings.MaxFileSizeMb} MB.");

    /// <summary>
    /// The file name without folders and control characters, at most 255 characters, with an extension that matches the
    /// content type when it has none.
    /// </summary>
    public static string CleanFileName(string? name, string? contentType)
    {
        var clean = new string(Path.GetFileName((name ?? string.Empty).Replace('\\', '/')).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0 || clean is "." or "..")
        {
            clean = "file";
        }
        if (Path.GetExtension(clean).Length == 0 && contentType is not null)
        {
            clean += contentType switch
            {
                FileTypes.Pdf => ".pdf",
                FileTypes.Jpeg => ".jpg",
                FileTypes.Png => ".png",
                FileTypes.Heic => ".heic",
                FileTypes.Webp => ".webp",
                _ => string.Empty
            };
        }
        if (clean.Length > DocumentFile.MaxFileNameLength)
        {
            var extension = Path.GetExtension(clean);
            extension = extension.Length > 20 ? string.Empty : extension;
            clean = clean[..(DocumentFile.MaxFileNameLength - extension.Length)] + extension;
        }
        return clean;
    }

    public async Task<PaginatedList<DocumentSummaryDto>> ListAsync(DocumentQuery query, CancellationToken cancellationToken)
    {
        PagingExtensions.Validate(query.Page, query.PageSize);
        IQueryable<Document> documents = db.Documents.AsNoTracking();

        if (DocumentDetails.ParseKind(query.Kind, "kind") is { } kind)
        {
            documents = documents.Where(d => d.Kind == kind);
        }
        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tags = DocumentTags.Normalize([query.Tag]);
            if (tags.Count != 1)
            {
                throw new InvalidInputException("tag", "Give one tag.");
            }
            var pattern = DocumentTags.Pattern(tags[0]);
            documents = documents.Where(d => d.Tags.Contains(pattern));
        }
        if (DocumentDetails.ParseDay(query.ExpiresFrom, "expiresFrom") is { } from)
        {
            documents = documents.Where(d => d.ExpiresOn >= from);
        }
        if (DocumentDetails.ParseDay(query.ExpiresTo, "expiresTo") is { } to)
        {
            documents = documents.Where(d => d.ExpiresOn <= to);
        }
        if (!string.IsNullOrWhiteSpace(query.Expiring))
        {
            // T86: the "Expire soon" list, with "today" of the server: from today to Expiry:SoonDays ahead.
            if (!string.Equals(query.Expiring.Trim(), "soon", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidInputException("expiring", "Use expiring=soon.");
            }
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), options.Value.GetTimeZone()).DateTime);
            var last = today.AddDays(options.Value.Expiry.SoonDays);
            documents = documents.Where(d => d.ExpiresOn >= today && d.ExpiresOn <= last);
        }

        var text = query.Text?.Trim() ?? string.Empty;
        if (text.Length > MaxSearchLength)
        {
            throw new InvalidInputException("q", $"The search text can have at most {MaxSearchLength} characters.");
        }

        IOrderedQueryable<Document> ordered;
        if (text.Length == 0)
        {
            ordered = documents.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id);
        }
        else if (db.Database.IsNpgsql())
        {
            // The full-text index: web search syntax ("two words", "exact phrase", -not), in the "english" and the
            // "simple" configurations, best match first.
            documents = documents.Where(d => d.SearchVector!.Matches(
                EF.Functions.WebSearchToTsQuery("english", text).Or(EF.Functions.WebSearchToTsQuery("simple", text))));
            ordered = documents
                .OrderByDescending(d => d.SearchVector!.Rank(
                    EF.Functions.WebSearchToTsQuery("english", text).Or(EF.Functions.WebSearchToTsQuery("simple", text))))
                .ThenByDescending(d => d.CreatedAt)
                .ThenByDescending(d => d.Id);
        }
        else
        {
            // The in-memory test database has no full-text index: each word must be in the title, the tags, the notes,
            // or the OCR text.
            foreach (var word in text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                documents = documents.Where(d =>
                    d.Title.ToLower().Contains(word) || d.Tags.Contains(word) ||
                    (d.Notes != null && d.Notes.ToLower().Contains(word)) ||
                    (d.OcrText != null && d.OcrText.ToLower().Contains(word)));
            }
            ordered = documents.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id);
        }

        var total = await documents.CountAsync(cancellationToken);
        var items = await ordered
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(Summary)
            .ToListAsync(cancellationToken);
        return new PaginatedList<DocumentSummaryDto> { Items = items, Page = query.Page, PageSize = query.PageSize, TotalCount = total };
    }

    public async Task<DocumentDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await FindAsync(id, cancellationToken) ?? throw new NotFoundException("Document", id);

    public Task<DocumentDto?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.Documents.AsNoTracking().Where(d => d.Id == id).Select(Details).FirstOrDefaultAsync(cancellationToken);

    public async Task<DocumentDto> UpdateAsync(Guid id, UpdateDocumentRequest request, CancellationToken cancellationToken)
    {
        var details = DocumentDetails.Create(request.Title, request.Kind, request.Tags, request.IssuedOn, request.ExpiresOn, request.Notes);
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw new NotFoundException("Document", id);
        var now = time.GetUtcNow();
        details.ApplyTo(document);
        document.UpdatedAt = now;
        db.RaiseEvent(new DocumentChanged(document.Id, DocumentChange.Details, document.Title, document.Kind, details.Tags,
            document.IssuedOn, document.ExpiresOn, document.OcrStatus, now));
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var document = await db.Documents.Include(d => d.Files).Include(d => d.Links)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw new NotFoundException("Document", id);
        var now = time.GetUtcNow();
        var keys = document.Files.Select(f => f.StorageKey).Distinct().ToList();
        // The links go first, so a consumer that follows links does not need to know about deleted documents.
        foreach (var link in document.Links)
        {
            db.RaiseEvent(new DocumentUnlinked(document.Id, link.Id, link.Kind, link.TargetId, now));
        }
        db.RaiseEvent(new DocumentDeleted(document.Id, document.Title, now));
        db.Documents.Remove(document);
        await db.SaveChangesAsync(cancellationToken);
        await RemoveUnusedAsync(keys);
        logger.LogInformation("Documents: deleted document {DocumentId}", id);
    }

    /// <summary>Deletes the stored files that no document uses (after a delete, or after a failed upload).</summary>
    private async Task RemoveUnusedAsync(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            try
            {
                if (!await db.Files.AsNoTracking().AnyAsync(f => f.StorageKey == key, CancellationToken.None))
                {
                    storage.Delete(key);
                }
            }
            catch (Exception exception)
            {
                // The rows are right; an unused file only takes space.
                logger.LogWarning(exception, "Documents: could not remove the unused file {StorageKey}", key);
            }
        }
    }

    public async Task<DocumentFileContent> GetFileAsync(Guid id, Guid fileId, CancellationToken cancellationToken)
    {
        var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId && f.DocumentId == id, cancellationToken)
            ?? throw new NotFoundException("File", fileId);
        var path = storage.PathOf(file.StorageKey);
        if (!File.Exists(path))
        {
            logger.LogError("Documents: the file {StorageKey} of document {DocumentId} is not on the volume", file.StorageKey, id);
            throw new NotFoundException("File", fileId);
        }
        return new DocumentFileContent(path, file.ContentType, file.FileName);
    }

    public async Task<DocumentDto> RetryOcrAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!Settings.Ocr.Enabled)
        {
            throw new ConflictException("The OCR is off on this host (Documents:Ocr:Enabled).");
        }
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw new NotFoundException("Document", id);
        document.OcrStatus = OcrStatus.Pending;
        document.OcrError = null;
        await db.SaveChangesAsync(cancellationToken);
        ocrSignal.Wake();
        return await GetAsync(id, cancellationToken);
    }

    public async Task<DocumentLinkDto> AddLinkAsync(Guid id, CreateDocumentLinkRequest request, CancellationToken cancellationToken)
    {
        if (!await db.Documents.AnyAsync(d => d.Id == id, cancellationToken))
        {
            throw new NotFoundException("Document", id);
        }
        var (kind, targetId) = await linkTargets.CheckAsync(request.Kind, request.TargetId, cancellationToken);
        if (await db.Links.AnyAsync(l => l.DocumentId == id && l.Kind == kind && l.TargetId == targetId, cancellationToken))
        {
            throw new ConflictException("The document has this link already.");
        }

        var link = new DocumentLink
        {
            Id = Guid.CreateVersion7(),
            DocumentId = id,
            Kind = kind,
            TargetId = targetId,
            CreatedAt = time.GetUtcNow()
        };
        db.Links.Add(link);
        db.RaiseEvent(new DocumentLinked(id, link.Id, link.Kind, link.TargetId, link.CreatedAt));
        await db.SaveChangesAsync(cancellationToken);
        return new DocumentLinkDto(link.Id, link.Kind, link.TargetId, link.CreatedAt);
    }

    public async Task RemoveLinkAsync(Guid id, Guid linkId, CancellationToken cancellationToken)
    {
        var link = await db.Links.FirstOrDefaultAsync(l => l.Id == linkId && l.DocumentId == id, cancellationToken)
            ?? throw new NotFoundException("Link", linkId);
        db.Links.Remove(link);
        db.RaiseEvent(new DocumentUnlinked(id, link.Id, link.Kind, link.TargetId, time.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentSummaryDto>> ListByLinkAsync(string? kind, string? targetId, CancellationToken cancellationToken)
    {
        var (linkKind, target) = LinkTargets.Normalize(kind, targetId);
        return await db.Documents.AsNoTracking()
            .Where(d => d.Links.Any(l => l.Kind == linkKind && l.TargetId == target))
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .Select(Summary)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The most target ids of one count request (T72).</summary>
    public const int MaxCountTargets = 200;

    /// <summary>
    /// How many documents link to each target (T72): every requested id (in its stored form) with its count, 0 included.
    /// </summary>
    /// <exception cref="InvalidInputException">An unknown kind, an id that this kind cannot have, or too many ids.</exception>
    public async Task<Dictionary<string, int>> CountByLinkAsync(DocumentLinkCountsRequest request, CancellationToken cancellationToken)
    {
        var ids = request.TargetIds ?? [];
        if (ids.Count > MaxCountTargets)
        {
            throw new InvalidInputException("targetIds", $"Ask for at most {MaxCountTargets} ids at once.");
        }
        var targets = new List<string>();
        string? kind = null;
        foreach (var id in ids)
        {
            (kind, var target) = LinkTargets.Normalize(request.Kind, id);
            targets.Add(target);
        }
        if (kind is null)
        {
            // No ids: still check the kind.
            LinkTargets.Normalize(request.Kind, Guid.NewGuid().ToString());
            return [];
        }
        var counts = await db.Links.AsNoTracking()
            .Where(l => l.Kind == kind && targets.Contains(l.TargetId))
            .GroupBy(l => l.TargetId)
            .Select(g => new { Target = g.Key, Count = g.Select(l => l.DocumentId).Distinct().Count() })
            .ToDictionaryAsync(c => c.Target, c => c.Count, cancellationToken);
        return targets.Distinct().ToDictionary(t => t, t => counts.GetValueOrDefault(t));
    }

    /// <summary>The documents with these ids, without the OCR text (T89), newest first.</summary>
    public async Task<IReadOnlyList<DocumentSummaryDto>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        return await db.Documents.AsNoTracking()
            .Where(d => ids.Contains(d.Id))
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .Select(Summary)
            .ToListAsync(cancellationToken);
    }

    internal static readonly Expression<Func<Document, DocumentSummaryDto>> Summary = d => new DocumentSummaryDto(
        d.Id, d.Title, d.Kind, DocumentTags.Read(d.Tags), d.IssuedOn, d.ExpiresOn, d.Notes, d.CreatedAt, d.UpdatedAt, d.OcrStatus,
        d.Files.OrderBy(f => f.Position)
            .Select(f => new DocumentFileDto(f.Id, f.FileName, f.ContentType, f.Size, f.Sha256, f.AddedAt)).ToList(),
        d.Links.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
            .Select(l => new DocumentLinkDto(l.Id, l.Kind, l.TargetId, l.CreatedAt)).ToList());

    internal static readonly Expression<Func<Document, DocumentDto>> Details = d => new DocumentDto(
        d.Id, d.Title, d.Kind, DocumentTags.Read(d.Tags), d.IssuedOn, d.ExpiresOn, d.Notes, d.CreatedAt, d.UpdatedAt, d.OcrStatus,
        d.OcrError, d.OcrText,
        d.Files.OrderBy(f => f.Position)
            .Select(f => new DocumentFileDto(f.Id, f.FileName, f.ContentType, f.Size, f.Sha256, f.AddedAt)).ToList(),
        d.Links.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
            .Select(l => new DocumentLinkDto(l.Id, l.Kind, l.TargetId, l.CreatedAt)).ToList());
}

/// <summary>The other modules' queries (<see cref="IDocumentQueries"/>).</summary>
public sealed class DocumentQueries(DocumentService documents) : IDocumentQueries
{
    public Task<DocumentDto?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        documents.FindAsync(id, cancellationToken);

    public async Task<IReadOnlyList<DocumentSummaryDto>> ListByLinkAsync(string kind, string targetId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await documents.ListByLinkAsync(kind, targetId, cancellationToken);
        }
        catch (InvalidInputException)
        {
            return [];
        }
    }

    public Task<IReadOnlyList<DocumentSummaryDto>> ListAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
        documents.ListByIdsAsync(ids, cancellationToken);
}
