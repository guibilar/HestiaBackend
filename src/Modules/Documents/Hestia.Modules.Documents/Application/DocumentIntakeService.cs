using Hestia.Modules.Documents.Contracts;
using Microsoft.AspNetCore.Http;

namespace Hestia.Modules.Documents.Application;

/// <summary>Adds a document for another module (<see cref="IDocumentIntake"/>, T102), with the same checks as an upload.</summary>
public sealed class DocumentIntakeService(DocumentService documents) : IDocumentIntake
{
    public Task<DocumentDto> AddAsync(DocumentIntake intake, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IFormFile> files = intake.Files
            .Select(file => (IFormFile)new FormFile(new MemoryStream(file.Content, writable: false), 0, file.Content.Length, "files", file.FileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = "application/octet-stream"
            })
            .ToList();
        return documents.UploadAsync(new DocumentUpload(intake.Title, null, intake.Tags, null, null, intake.Notes, files), cancellationToken);
    }

    public Task RemoveAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        documents.DeleteAsync(documentId, cancellationToken);
}
