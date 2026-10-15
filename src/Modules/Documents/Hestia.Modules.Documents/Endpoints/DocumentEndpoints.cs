using Hestia.Modules.Documents.Application;
using Hestia.Modules.Documents.Contracts;
using Hestia.Platform.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Hestia.Modules.Documents.Endpoints;

/// <summary>The Documents endpoints, under /api/documents. A read-only user can call the GET endpoints only.</summary>
public static class DocumentEndpoints
{
    public const string Prefix = "/api/documents";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var documents = endpoints.MapGroup(Prefix).WithTags("Documents");

        documents.MapPost("", async Task<Created<DocumentDto>> (HttpRequest request, DocumentService service,
                IOptions<DocumentsOptions> options, CancellationToken cancellationToken) =>
            {
                var upload = await ReadUploadAsync(request, options.Value, cancellationToken);
                var document = await service.UploadAsync(upload, cancellationToken);
                return TypedResults.Created($"{Prefix}/{document.Id}", document);
            })
            .WithName("UploadDocument")
            .WithSummary("Uploads a document: one or more files and its details.")
            .WithDescription(
                "multipart/form-data with the fields: files (one or more PDF, JPEG, PNG, HEIC, or WebP files), title " +
                "(default: the name of the first file), kind (Receipt, Warranty, Lease, Tax, Insurance, Manual, Other; " +
                "default Other), tags (repeat the field, or separate them with commas), issuedOn and expiresOn (yyyy-MM-dd), " +
                "and notes. The answer comes at once, with ocrStatus Pending: the OCR runs in the background.");

        documents.MapGet("", (DocumentService service, CancellationToken cancellationToken, string? kind, string? tag,
                string? q, string? expiresFrom, string? expiresTo, string? expiring, int page = 1, int pageSize = 20) =>
                service.ListAsync(new DocumentQuery(kind, tag, q, expiresFrom, expiresTo, page, pageSize, expiring), cancellationToken))
            .WithName("ListDocuments")
            .WithSummary("A page of documents, newest first (best match first with q).")
            .WithDescription(
                "Filters: kind, tag, q (full-text search in the title, the tags, the notes, and the OCR text; web search " +
                "syntax: \"exact phrase\", -word), expiresFrom and expiresTo (yyyy-MM-dd, both included), expiring=soon (from " +
                "today, in the host's time zone, to Documents:Expiry:SoonDays ahead; T86).");

        documents.MapGet("/by-link", (DocumentService service, CancellationToken cancellationToken, string? kind, string? id) =>
                service.ListByLinkAsync(kind, id, cancellationToken))
            .WithName("ListDocumentsByLink")
            .WithSummary("The documents linked to one thing, for example ?kind=Money.Transaction&id=<transaction id>.");

        documents.MapGet("/{id:guid}", (Guid id, DocumentService service, CancellationToken cancellationToken) =>
                service.GetAsync(id, cancellationToken))
            .WithName("GetDocument")
            .WithSummary("One document, with its OCR text.");

        documents.MapPut("/{id:guid}", (Guid id, UpdateDocumentRequest request, DocumentService service,
                CancellationToken cancellationToken) => service.UpdateAsync(id, request, cancellationToken))
            .WithName("UpdateDocument")
            .WithSummary("Changes the details of a document (all of them). The files stay.");

        documents.MapDelete("/{id:guid}", async Task<NoContent> (Guid id, DocumentService service, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteDocument")
            .WithSummary("Deletes a document, its links, and its files (a file stays while another document has the same content).");

        documents.MapGet("/{id:guid}/files/{fileId:guid}", async Task<PhysicalFileHttpResult> (Guid id, Guid fileId,
                HttpContext context, DocumentService service, CancellationToken cancellationToken, bool inline = false) =>
            {
                var file = await service.GetFileAsync(id, fileId, cancellationToken);
                context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
                context.Response.Headers[HeaderNames.CacheControl] = "private, max-age=3600";
                if (inline)
                {
                    // For a preview in the browser (a PDF viewer, an image).
                    var disposition = new ContentDispositionHeaderValue("inline");
                    disposition.SetHttpFileName(file.FileName);
                    context.Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
                    return TypedResults.PhysicalFile(file.Path, file.ContentType, enableRangeProcessing: true);
                }
                return TypedResults.PhysicalFile(file.Path, file.ContentType, file.FileName, enableRangeProcessing: true);
            })
            .WithName("DownloadDocumentFile")
            .WithSummary("The content of one file. inline=true shows it in the browser; the default is a download.");

        documents.MapPost("/{id:guid}/ocr", async Task<Accepted<DocumentDto>> (Guid id, DocumentService service,
                CancellationToken cancellationToken) =>
                TypedResults.Accepted($"{Prefix}/{id}", await service.RetryOcrAsync(id, cancellationToken)))
            .WithName("RetryDocumentOcr")
            .WithSummary("Runs the OCR of a document again, in the background (ocrStatus Pending).");

        documents.MapPost("/{id:guid}/links", async Task<Created<DocumentLinkDto>> (Guid id, CreateDocumentLinkRequest request,
                DocumentService service, CancellationToken cancellationToken) =>
            {
                var link = await service.AddLinkAsync(id, request, cancellationToken);
                return TypedResults.Created($"{Prefix}/{id}/links/{link.Id}", link);
            })
            .WithName("AddDocumentLink")
            .WithSummary("Links the document to something outside the module, for example { \"kind\": \"Money.Transaction\", \"targetId\": \"<id>\" }.")
            .WithDescription("For a finance transaction, the host checks with finance that the transaction exists (as the signed-in user).");

        documents.MapDelete("/{id:guid}/links/{linkId:guid}", async Task<NoContent> (Guid id, Guid linkId, DocumentService service,
                CancellationToken cancellationToken) =>
            {
                await service.RemoveLinkAsync(id, linkId, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("RemoveDocumentLink")
            .WithSummary("Removes a link of the document.");
    }

    /// <summary>
    /// Reads the upload form. Not with form binding: the minimal API form binding asks for an antiforgery token, and
    /// these endpoints use the session cookie with SameSite=Strict instead (D5).
    /// </summary>
    private static async Task<DocumentUpload> ReadUploadAsync(HttpRequest request, DocumentsOptions options,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            throw new InvalidInputException("files", "Send the document as multipart/form-data, with the files in the field \"files\".");
        }
        // Kestrel allows 30 MB by default. The upload limit is a setting.
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = options.MaxUploadSize;
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = options.MaxUploadSize }, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            throw new InvalidInputException("files", $"The upload is larger than {options.MaxUploadSizeMb} MB.");
        }

        return new DocumentUpload(
            Value(form, "title"),
            Value(form, "kind"),
            form["tags"].OfType<string>().ToList(),
            Value(form, "issuedOn"),
            Value(form, "expiresOn"),
            Value(form, "notes"),
            form.Files.ToList());
    }

    private static string? Value(IFormCollection form, string name) =>
        form.TryGetValue(name, out var values) && values.Count > 0 ? values[^1] : null;
}
