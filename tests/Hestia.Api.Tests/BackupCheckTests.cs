using System.Text.Json;
using Hestia.Api.Backups;
using Hestia.Api.Events;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hestia.Api.Tests;

/// <summary>
/// T99: the host reads the status files of the backup jobs (<c>docker/backup.sh</c>) for the readiness check
/// <c>backups</c> and for System alerts on Today.
/// </summary>
public sealed class BackupCheckTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);
    private static readonly BackupsOptions Settings = new();

    private readonly string _folder = Directory.CreateTempSubdirectory("hestia-backup-status-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>A status file as backup.sh writes it: one value per line, null for none.</summary>
    private void WriteStatus(string relativePath, string name, DateTimeOffset? backupAt, bool? backupOk = true,
        string? error = null, DateTimeOffset? verifyAt = null, bool? verifyOk = null, string? verifyMessage = null)
    {
        static string Text(string? value) => value is null ? "null" : $"\"{value}\"";
        static string Time(DateTimeOffset? value) => value is null ? "null" : $"\"{value.Value.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}\"";
        static string Bool(bool? value) => value is null ? "null" : value.Value ? "true" : "false";
        var path = Path.Combine(_folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            {
              "name": "{{name}}",
              "lastBackupAt": {{Time(backupAt)}},
              "lastBackupOk": {{Bool(backupOk)}},
              "lastBackupFile": "{{name}}-2026-10-16_030000.dump",
              "lastBackupBytes": 1516,
              "lastBackupError": {{Text(error)}},
              "lastVerifyAt": {{Time(verifyAt)}},
              "lastVerifyOk": {{Bool(verifyOk)}},
              "lastVerifyMessage": {{Text(verifyMessage)}}
            }
            """);
    }

    private BackupStatusReader Reader() =>
        new(new FixedOptions<BackupsOptions>(new BackupsOptions { StatusPath = _folder }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupStatusReader>.Instance);

    [Fact]
    public void RecentBackups_ThatRestore_HaveNoProblem()
    {
        WriteStatus("hestia/hestia.json", "hestia", Now.AddHours(-12), verifyAt: Now.AddHours(-12), verifyOk: true, verifyMessage: "ok");
        WriteStatus("finance/finance.json", "finance", Now.AddHours(-13));

        var reader = Reader();

        Assert.Equal(["finance", "hestia"], reader.Read().Select(status => status.Name));
        Assert.Empty(reader.Problems(Now));
    }

    [Fact]
    public void EachProblem_IsFound()
    {
        var files = new List<BackupStatusFile>
        {
            new("failed", Now.AddHours(-1), false, null, null, "pg_dump failed", null, null, null),
            new("old", Now.AddHours(-30), true, "old.dump", 10, null, Now.AddHours(-30), true, "ok"),
            new("broken", Now.AddHours(-1), true, "broken.dump", 10, null, Now.AddHours(-1), false, "2 of 5 document files are not there."),
            new("unchecked", Now.AddHours(-1), true, "unchecked.dump", 10, null, Now.AddDays(-4), true, "ok"),
            new("never", null, null, null, null, null, null, null, null)
        };

        var problems = BackupStatusReader.Problems(files, Now, Settings);

        Assert.Equal(
            [
                ("failed", BackupProblemKind.BackupFailed),
                ("old", BackupProblemKind.BackupOld),
                ("broken", BackupProblemKind.RestoreCheckFailed),
                ("unchecked", BackupProblemKind.RestoreCheckOld),
                ("never", BackupProblemKind.NoStatus)
            ],
            problems.Select(problem => (problem.Name, problem.Kind)));
        Assert.Equal("pg_dump failed", problems[0].Message);
        Assert.Equal("2 of 5 document files are not there.", problems[2].Message);
    }

    [Fact]
    public void NoStatusFile_IsAProblem_AndABadFileIsLeftOut()
    {
        File.WriteAllText(Path.Combine(_folder, "broken.json"), "{ not json");

        var problems = Reader().Problems(Now);

        Assert.Equal(BackupProblemKind.NoStatus, Assert.Single(problems).Kind);
    }

    [Fact]
    public void TheSameFolderMountedTwice_GivesOneSet()
    {
        WriteStatus("hestia/hestia.json", "hestia", Now.AddHours(-1));
        WriteStatus("finance/hestia.json", "hestia", Now.AddHours(-1));

        Assert.Single(Reader().Read());
    }

    [Fact]
    public void Alerts_HaveTheDayInTheHostZone_AndTheMessage()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Edmonton");
        var failed = SystemHealthAlerts.BackupAlert(
            new BackupProblem("Finance", BackupProblemKind.RestoreCheckFailed, new DateTimeOffset(2026, 10, 16, 3, 0, 0, TimeSpan.Zero), "pg_restore failed"),
            zone);
        var missing = SystemHealthAlerts.BackupAlert(new BackupProblem("hestia", BackupProblemKind.NoStatus, null, null), zone);

        Assert.Equal(("system.restore-check.finance", "restore-check-failed", AlertSeverity.Bad), (failed.Key, failed.Kind, failed.Severity));
        // 03:00 UTC is the evening before in Edmonton.
        Assert.Equal(AlertValue.Day(new DateOnly(2026, 10, 15)), failed.Values["date"]);
        Assert.Equal(AlertValue.Plain("pg_restore failed"), failed.Values["message"]);
        Assert.Contains("{message}", failed.Text);
        Assert.Equal(("system.backup.hestia", "backup-missing"), (missing.Key, missing.Kind));
        Assert.DoesNotContain("{date}", missing.Text);
    }

    [Fact]
    public async Task TheHost_HasABackupsCheck_AndAnAlert_OnlyWithAStatusFolder()
    {
        WriteStatus("hestia.json", "hestia", Now.AddDays(-2));
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = Host(finance, new Dictionary<string, string?> { ["Backups:StatusPath"] = _folder });
        var client = factory.CreateClient();

        var ready = await ReadyChecksAsync(client);
        Assert.Equal("Degraded", ready["backups"]);
        await factory.Services.GetRequiredService<IAlertChecks>().CheckAsync(SystemHealthAlerts.SourceName);
        var alert = Assert.Single((await (await client.GetAsync("/api/alerts")).ReadAsync<AlertList>()).Alerts,
            a => a.Id.StartsWith(SystemHealthAlerts.BackupPrefix, StringComparison.Ordinal));
        Assert.Equal(("system.backup.hestia", "backup-old"), (alert.Id, alert.Kind));

        // The next night's backup worked: the check is healthy, and the alert resolves.
        WriteStatus("hestia.json", "hestia", Now.AddHours(-1));
        Assert.Equal("Healthy", (await ReadyChecksAsync(client))["backups"]);
        await factory.Services.GetRequiredService<IAlertChecks>().CheckAsync(SystemHealthAlerts.SourceName);
        Assert.DoesNotContain((await (await client.GetAsync("/api/alerts")).ReadAsync<AlertList>()).Alerts,
            a => a.Id.StartsWith(SystemHealthAlerts.BackupPrefix, StringComparison.Ordinal));

        await using var without = Host(finance, []);
        Assert.DoesNotContain("backups", (await ReadyChecksAsync(without.CreateClient())).Keys);
    }

    private static HestiaApiFactory Host(FakeFinance finance, Dictionary<string, string?> settings) =>
        new(finance.HostSettings(settings), services =>
        {
            foreach (var source in services.Where(s => s.ServiceType == typeof(IAlertSource) &&
                         s.ImplementationType != typeof(SystemHealthAlerts)).ToList())
            {
                services.Remove(source);
            }
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new TestClock(Now));
        });

    private static async Task<Dictionary<string, string>> ReadyChecksAsync(HttpClient client)
    {
        using var body = JsonDocument.Parse(await (await client.GetAsync("/health/ready")).Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(check => check.GetProperty("name").GetString()!, check => check.GetProperty("status").GetString()!);
    }
}
