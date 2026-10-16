using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Hestia.Api.Backups;

/// <summary>The settings of the backup checks (section <c>Backups</c>, T99).</summary>
public sealed class BackupsOptions
{
    public const string SectionName = "Backups";

    /// <summary>
    /// The folder with the status files of the backup jobs (<c>&lt;name&gt;.json</c>, also one folder down). Docker:
    /// <c>/status</c>. Empty: the host does not check the backups (no readiness check, no alerts).
    /// </summary>
    public string? StatusPath { get; set; }

    /// <summary>The age after which the newest backup is too old. The jobs run every night, so a missed night shows.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(26);

    /// <summary>The age after which the newest restore check is too old (it runs after each nightly backup).</summary>
    public TimeSpan VerifyMaxAge { get; set; } = TimeSpan.FromDays(3);
}

/// <summary>
/// One status file of a backup job (T99): written by <c>docker/backup.sh</c> of Hestia (<c>hestia.json</c>) and of
/// finance (<c>finance.json</c>) after each backup and each restore check.
/// </summary>
/// <param name="Name">The backup set, for example <c>hestia</c>.</param>
/// <param name="LastBackupAt">When the newest backup ran (UTC).</param>
/// <param name="LastBackupOk">True when it worked.</param>
/// <param name="LastBackupFile">The dump file, when it worked.</param>
/// <param name="LastBackupBytes">The size of the dump.</param>
/// <param name="LastBackupError">Why it failed.</param>
/// <param name="LastVerifyAt">When the newest restore check ran. Null: the check is off.</param>
/// <param name="LastVerifyOk">True when the dump restored and all its files were there.</param>
/// <param name="LastVerifyMessage">What the check found.</param>
public sealed record BackupStatusFile(
    string? Name,
    DateTimeOffset? LastBackupAt,
    bool? LastBackupOk,
    string? LastBackupFile,
    long? LastBackupBytes,
    string? LastBackupError,
    DateTimeOffset? LastVerifyAt,
    bool? LastVerifyOk,
    string? LastVerifyMessage);

/// <summary>What is wrong with one backup set.</summary>
public enum BackupProblemKind
{
    /// <summary>The newest backup failed.</summary>
    BackupFailed,

    /// <summary>The newest backup is older than <see cref="BackupsOptions.MaxAge"/>: the job did not run.</summary>
    BackupOld,

    /// <summary>The newest restore check failed: the dump does not restore, or files are missing.</summary>
    RestoreCheckFailed,

    /// <summary>The newest restore check is older than <see cref="BackupsOptions.VerifyMaxAge"/>.</summary>
    RestoreCheckOld,

    /// <summary>No status file: the backup job never ran, or its folder is not mounted.</summary>
    NoStatus
}

/// <param name="Name">The backup set, for example <c>hestia</c>.</param>
/// <param name="Kind">What is wrong.</param>
/// <param name="Since">The moment of the backup or the check that the problem is about, if known.</param>
/// <param name="Message">The message of the job, if any.</param>
public sealed record BackupProblem(string Name, BackupProblemKind Kind, DateTimeOffset? Since, string? Message);

/// <summary>Reads the status files of the backup jobs, and finds the problems (T99).</summary>
public sealed class BackupStatusReader(IOptionsMonitor<BackupsOptions> options, ILogger<BackupStatusReader> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>True when the host checks the backups (<see cref="BackupsOptions.StatusPath"/> has a value).</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(options.CurrentValue.StatusPath);

    /// <summary>
    /// The status files in the folder and one level down, one per backup set (the newest backup wins when two files
    /// have the same name, for example the same folder mounted twice). A file that does not parse is left out.
    /// </summary>
    public IReadOnlyList<BackupStatusFile> Read()
    {
        var path = options.CurrentValue.StatusPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return [];
        }
        var files = new List<BackupStatusFile>();
        foreach (var file in Directory.EnumerateFiles(path, "*.json", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1 }))
        {
            try
            {
                var status = JsonSerializer.Deserialize<BackupStatusFile>(File.ReadAllText(file), Json);
                if (status is not null)
                {
                    files.Add(status with { Name = string.IsNullOrWhiteSpace(status.Name) ? Path.GetFileNameWithoutExtension(file) : status.Name.Trim() });
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Backups: the status file {File} could not be read: {Message}", file, exception.Message);
            }
        }
        return files
            .GroupBy(status => status.Name!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(status => status.LastBackupAt).First())
            .OrderBy(status => status.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The problems of all backup sets at <paramref name="now"/>. No status file at all is one problem.</summary>
    public IReadOnlyList<BackupProblem> Problems(DateTimeOffset now) => Problems(Read(), now, options.CurrentValue);

    public static IReadOnlyList<BackupProblem> Problems(IReadOnlyList<BackupStatusFile> files, DateTimeOffset now, BackupsOptions settings)
    {
        if (files.Count == 0)
        {
            return [new BackupProblem("hestia", BackupProblemKind.NoStatus, null, null)];
        }
        var problems = new List<BackupProblem>();
        foreach (var file in files)
        {
            var name = file.Name!;
            if (file.LastBackupAt is not { } backupAt)
            {
                problems.Add(new BackupProblem(name, BackupProblemKind.NoStatus, null, null));
            }
            else if (file.LastBackupOk != true)
            {
                problems.Add(new BackupProblem(name, BackupProblemKind.BackupFailed, backupAt, file.LastBackupError));
            }
            else if (now - backupAt > settings.MaxAge)
            {
                problems.Add(new BackupProblem(name, BackupProblemKind.BackupOld, backupAt, null));
            }

            // No restore check yet (or the check is off): nothing to say about it.
            if (file.LastVerifyAt is { } verifyAt)
            {
                if (file.LastVerifyOk != true)
                {
                    problems.Add(new BackupProblem(name, BackupProblemKind.RestoreCheckFailed, verifyAt, file.LastVerifyMessage));
                }
                else if (now - verifyAt > settings.VerifyMaxAge)
                {
                    problems.Add(new BackupProblem(name, BackupProblemKind.RestoreCheckOld, verifyAt, null));
                }
            }
        }
        return problems;
    }
}

/// <summary>
/// The readiness check <c>backups</c> (T99): Degraded (still 200) when a backup failed, is too old, or does not restore.
/// Only when <see cref="BackupsOptions.StatusPath"/> is set.
/// </summary>
public sealed class BackupsHealthCheck(BackupStatusReader reader, TimeProvider time) : IHealthCheck
{
    public const string Name = "backups";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var problems = reader.Problems(time.GetUtcNow());
        return Task.FromResult(problems.Count == 0
            ? HealthCheckResult.Healthy("All backups are recent and restore.")
            : HealthCheckResult.Degraded(string.Join("; ", problems.Select(p => $"{p.Name}: {p.Kind}"))));
    }
}
