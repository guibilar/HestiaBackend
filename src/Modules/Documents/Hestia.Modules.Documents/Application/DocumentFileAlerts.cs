using Hestia.Modules.Alerts.Contracts;

namespace Hestia.Modules.Documents.Application;

/// <summary>The result of the last scheduled file check (<see cref="FileCheckWorker"/>).</summary>
/// <param name="CheckedAt">When the check ran.</param>
/// <param name="Missing">The <c>DocumentFiles</c> rows whose file was not on the volume.</param>
public sealed record FileCheckResult(DateTimeOffset CheckedAt, IReadOnlyList<MissingFile> Missing);

/// <summary>
/// The last result of the scheduled file check (T80), for <see cref="DocumentFileAlerts"/>. It is in memory: after a
/// restart of the host, the result is not known until the next check (<c>Documents:FileCheck:FirstDelay</c>), and the
/// open alert stays as it is meanwhile.
/// </summary>
public sealed class FileCheckState
{
    private volatile FileCheckResult? _last;

    /// <summary>The last result, or null when no check ran since the host started.</summary>
    public FileCheckResult? Last => _last;

    public void Record(FileCheckResult result) => _last = result;
}

/// <summary>
/// The alert <c>documents.files-missing</c> (T80, severity Bad): the last scheduled file check found
/// <c>DocumentFiles</c> rows whose file is not on the documents volume. Such a document fails to download.
/// </summary>
/// <remarks>
/// The check does not read the volume: it uses the result of the last <see cref="FileCheckWorker"/> run (weekly). At
/// each alert check, it looks again at only those rows and files, so the alert goes when the user restores the files or
/// deletes the documents. A new missing file shows after the next scheduled check.
/// </remarks>
public sealed class DocumentFileAlerts(FileCheckState state, DocumentFileCheck check) : IAlertSource
{
    public const string SourceName = "Documents.Files";
    public const string Key = "documents.files-missing";

    public string Name => SourceName;

    public string Module => DocumentExpiryAlerts.ModuleName;

    public int Order => 100;

    public async Task<AlertCheckResult> CheckAsync(AlertCheckContext context, CancellationToken cancellationToken)
    {
        if (context.Target is not null || state.Last is not { } last)
        {
            // An event about one document, or no file check since the host started: nothing new is known.
            return new AlertCheckResult([], []);
        }

        var missing = await check.StillMissingAsync(last.Missing, cancellationToken);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(last.CheckedAt, context.TimeZone).DateTime);
        IReadOnlyList<AlertCandidate> alerts = missing.Count == 0 ? [] : [Alert(missing.Count, day)];
        return new AlertCheckResult(alerts, [Key]);
    }

    public static AlertCandidate Alert(int count, DateOnly checkedOn) =>
        new(Key, "files-missing", "Document files are missing",
            "{count} document file(s) are not on the documents volume (file check on {date}). These documents do not " +
            "download. Restore the files from a backup, or delete the documents. To see them, run: " +
            "docker compose run --rm -T hestia-api documents-clean-files --check-missing",
            new Dictionary<string, AlertValue>
            {
                ["count"] = AlertValue.Count(count),
                ["date"] = AlertValue.Day(checkedOn)
            },
            AlertSeverity.Bad, Link: null,
            // Before the expiry alerts of the module (their sort keys start with a date).
            SortKey: "0");
}
