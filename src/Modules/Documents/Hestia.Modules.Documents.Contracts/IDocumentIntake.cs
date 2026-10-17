namespace Hestia.Modules.Documents.Contracts;

/// <summary>
/// How another module adds a document from files that it has, outside an upload request (T102: the attachments of an
/// email in the Inbox). The Documents module registers the implementation. The document gets the OCR as an upload does.
/// </summary>
public interface IDocumentIntake
{
    /// <summary>Adds a document with one or more files.</summary>
    /// <returns>The new document.</returns>
    /// <exception cref="Hestia.Platform.Exceptions.InvalidInputException">A file type that Documents does not take, or a file
    /// that is too big.</exception>
    Task<DocumentDto> AddAsync(DocumentIntake intake, CancellationToken cancellationToken = default);

    /// <summary>Removes a document that <see cref="AddAsync"/> added, when the work it was for failed (the note of the email).</summary>
    /// <exception cref="Hestia.Platform.Exceptions.NotFoundException">No such document.</exception>
    Task RemoveAsync(Guid documentId, CancellationToken cancellationToken = default);
}

/// <summary>A document to add from files.</summary>
/// <param name="Title">The title; null: the name of the first file.</param>
/// <param name="Tags">The tags, for example <c>email</c>.</param>
/// <param name="Notes">Free text, for example where the document came from.</param>
/// <param name="Files">The files.</param>
public sealed record DocumentIntake(string? Title, IReadOnlyList<string> Tags, string? Notes, IReadOnlyList<DocumentIntakeFile> Files);

/// <summary>One file of a <see cref="DocumentIntake"/>. The type comes from the content, as for an upload.</summary>
public sealed record DocumentIntakeFile(string FileName, byte[] Content);
