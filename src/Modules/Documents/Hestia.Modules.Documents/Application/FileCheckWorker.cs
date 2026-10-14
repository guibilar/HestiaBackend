using Hestia.Modules.Alerts.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Documents.Application;

/// <summary>
/// The scheduled check of the documents volume (T64). Some minutes after the start (<c>Documents:FileCheck:FirstDelay</c>)
/// and then every <c>Documents:FileCheck:Interval</c> (default 7 days), it logs a warning:
/// <list type="bullet">
/// <item>with the count and the bytes of the unused files (as <c>documents-clean-files</c>; it deletes nothing);</item>
/// <item>for each <c>DocumentFiles</c> row whose file is not on the volume (document id, file id).</item>
/// </list>
/// A healthy volume gives one information line. A failure of a run is an error in the log; the next run comes at the
/// normal time.
/// <para>T80: each run stores its missing rows in <see cref="FileCheckState"/>, and asks the Alerts module for a check
/// of <see cref="DocumentFileAlerts"/>, so the alert <c>documents.files-missing</c> shows (or goes) at once.</para>
/// </summary>
public sealed class FileCheckWorker(
    DocumentFileCheck check,
    FileCheckState state,
    IAlertChecks alerts,
    IOptions<DocumentsOptions> options,
    TimeProvider time,
    ILogger<FileCheckWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.FileCheck;
        if (!settings.Enabled)
        {
            logger.LogInformation("Documents: the scheduled file check is off (Documents:FileCheck:Enabled)");
            return;
        }

        var delay = settings.FirstDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = settings.Interval;

            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Documents: the file check failed. The next check is in {Interval}", settings.Interval);
            }
        }
    }

    /// <summary>One check: logs the unused files and the missing files. Returns what it found.</summary>
    public async Task<(UnusedFiles Unused, IReadOnlyList<MissingFile> Missing)> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!check.StorageExists)
        {
            // The readiness check documents-storage reports this too.
            logger.LogWarning("Documents: the file check cannot run, because the documents folder does not exist (Documents:StoragePath)");
            return (new UnusedFiles([], []), Array.Empty<MissingFile>());
        }

        var unused = await check.FindUnusedAsync(cancellationToken);
        if (unused.Unused.Count > 0)
        {
            logger.LogWarning(
                "Documents: {Count} unused file(s) on the documents volume, {Bytes} bytes. " +
                "To delete them, run documents-clean-files --delete",
                unused.Unused.Count, unused.Bytes);
        }

        var missing = await check.FindMissingAsync(cancellationToken);
        state.Record(new FileCheckResult(time.GetUtcNow(), missing));
        alerts.RequestCheck(DocumentFileAlerts.SourceName);
        foreach (var row in missing)
        {
            logger.LogWarning(
                "Documents: the file {FileId} of document {DocumentId} is not on the documents volume. " +
                "Restore it from a backup, or delete the document",
                row.FileId, row.DocumentId);
        }

        if (unused.Unused.Count == 0 && missing.Count == 0)
        {
            logger.LogInformation("Documents: file check done. No unused files and no missing files");
        }
        return (unused, missing);
    }
}
