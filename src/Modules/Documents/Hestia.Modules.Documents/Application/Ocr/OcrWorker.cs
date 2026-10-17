using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Documents.Domain;
using Hestia.Modules.Documents.Infrastructure;
using Hestia.Platform.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Documents.Application.Ocr;

/// <summary>Wakes the OCR worker when a document needs OCR, so it does not wait for the next poll.</summary>
public sealed class OcrSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        if (_signal.CurrentCount > 0)
        {
            return;
        }
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another upload woke the worker at the same time.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// The background OCR (D9): not in the upload request. It takes the documents with the status Pending, oldest first, one
/// at a time, and runs <see cref="DocumentOcr"/> on each. The status is in the database, so a restart loses no work:
/// the next start reads the documents that are still Pending.
/// </summary>
public sealed class OcrWorker(
    IServiceScopeFactory scopes,
    OcrSignal signal,
    IOptions<DocumentsOptions> options,
    ILogger<OcrWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.Ocr;
        if (!settings.Enabled)
        {
            logger.LogInformation("Documents: the OCR is off (Documents:Ocr:Enabled). New documents get the status NotNeeded");
            return;
        }
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await ReadNextAsync(stoppingToken))
                {
                    // One document after the other, until none is Pending.
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Documents: the OCR worker failed. It tries again in {Interval}", settings.PollInterval);
            }

            try
            {
                await signal.WaitAsync(settings.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> ReadNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DocumentOcr>().ReadNextAsync(cancellationToken);
    }
}

/// <summary>The OCR of one document: the text of all its files, the status, and the event <see cref="DocumentChanged"/>.</summary>
public sealed class DocumentOcr(
    DocumentsDbContext db,
    DocumentStorage storage,
    IOcrEngine engine,
    TimeProvider time,
    ILogger<DocumentOcr> logger)
{
    /// <summary>Reads the oldest Pending document. Returns false when no document is Pending.</summary>
    public async Task<bool> ReadNextAsync(CancellationToken cancellationToken)
    {
        var document = await db.Documents
            .Include(d => d.Files)
            .Where(d => d.OcrStatus == OcrStatus.Pending)
            .OrderBy(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return false;
        }

        var texts = new List<string>();
        var errors = new List<string>();
        foreach (var file in document.Files.OrderBy(f => f.Position))
        {
            var result = await ReadFileAsync(file, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                texts.Add(result.Text.Trim());
            }
            if (!result.Success)
            {
                errors.Add($"{file.FileName}: {result.Error}");
            }
        }

        var text = string.Join("\n\n", texts);
        document.OcrText = text.Length == 0 ? null : TextCut.Start(text, Document.MaxOcrTextLength);
        document.OcrStatus = errors.Count == 0 ? OcrStatus.Done : OcrStatus.Failed;
        var error = string.Join("\n", errors);
        document.OcrError = errors.Count == 0 ? null : TextCut.Start(error, Document.MaxOcrErrorLength);
        db.RaiseEvent(new DocumentChanged(document.Id, DocumentChange.OcrText, document.Title, document.Kind,
            DocumentTags.Read(document.Tags), document.IssuedOn, document.ExpiresOn, document.OcrStatus, time.GetUtcNow()));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The user deleted the document during the OCR.
            logger.LogInformation("Documents: document {DocumentId} was deleted during its OCR", document.Id);
            return true;
        }

        if (document.OcrStatus == OcrStatus.Done)
        {
            logger.LogInformation("Documents: OCR of document {DocumentId} done ({Characters} characters)", document.Id,
                document.OcrText?.Length ?? 0);
        }
        else
        {
            logger.LogWarning("Documents: OCR of document {DocumentId} failed: {Error}", document.Id, document.OcrError);
        }
        return true;
    }

    private async Task<OcrResult> ReadFileAsync(DocumentFile file, CancellationToken cancellationToken)
    {
        var path = storage.PathOf(file.StorageKey);
        if (!File.Exists(path))
        {
            logger.LogError("Documents: the file {StorageKey} of document {DocumentId} is not on the volume", file.StorageKey, file.DocumentId);
            return OcrResult.Failed("The file is not in the storage.");
        }
        try
        {
            return await engine.ExtractTextAsync(path, file.ContentType, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Documents: the OCR of file {FileId} failed", file.Id);
            return OcrResult.Failed(exception.Message);
        }
    }
}
