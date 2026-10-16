using Hestia.Api.Backups;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Platform.Events;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hestia.Api.Events;

/// <summary>
/// The alert rules of the host itself (T80), module <c>System</c>: event problems that the user must fix, so that they
/// show on Today and not only in the log.
/// <list type="bullet">
/// <item><c>system.outbox-stuck.&lt;schema&gt;</c> (Bad): a module outbox has rows that failed
/// <c>Events:Outbox:MaxAttempts</c> times. The sender does not try them again; <c>retry-outbox &lt;schema&gt;</c> does.</item>
/// <item><c>system.broker-down</c> (Warning): the host has had no working connection to the RabbitMQ broker for more
/// than <c>Events:Broker:AlertAfter</c> (30 minutes).</item>
/// <item><c>system.backup.&lt;name&gt;</c> (Bad, T99): the newest backup of a set (<c>hestia</c>, <c>finance</c>)
/// failed, is too old, or has no status file.</item>
/// <item><c>system.restore-check.&lt;name&gt;</c> (Bad, T99): the newest restore check of a set failed, or is too old.</item>
/// </list>
/// </summary>
/// <remarks>
/// The rules are in the host, not in a module: they read the state of the host (the broker connection, the
/// <c>Events</c> settings, and the outbox rows of all modules). A module cannot see these (D6), and
/// <c>Hestia.Platform</c> must not reference the contracts of a module.
/// </remarks>
public sealed class SystemHealthAlerts(
    IServiceProvider services,
    IEnumerable<ModuleDatabase> databases,
    IOptionsMonitor<EventsOptions> options,
    BrokerConnectionState broker,
    BackupStatusReader backups,
    ILogger<SystemHealthAlerts> logger) : IAlertSource
{
    public const string SourceName = "System.Health";
    public const string ModuleName = "System";
    public const string OutboxPrefix = "system.outbox-stuck.";
    public const string BrokerKey = "system.broker-down";
    public const string BackupPrefix = "system.backup.";
    public const string RestoreCheckPrefix = "system.restore-check.";

    public string Name => SourceName;

    public string Module => ModuleName;

    /// <summary>After the modules of the user: these alerts are rare, and they are about the host, not about data.</summary>
    public int Order => 900;

    public async Task<AlertCheckResult> CheckAsync(AlertCheckContext context, CancellationToken cancellationToken)
    {
        var alerts = new List<AlertCandidate>();
        var covered = new List<string>();
        var complete = true;
        var settings = options.CurrentValue;

        foreach (var database in databases.Where(database => database.DurableEvents).OrderBy(d => d.Schema, StringComparer.Ordinal))
        {
            try
            {
                var outbox = ((DbContext)services.GetRequiredService(database.ContextType)).Set<OutboxMessage>().AsNoTracking();
                var failed = await outbox.CountAsync(row => row.Attempts >= settings.Outbox.MaxAttempts, cancellationToken);
                covered.Add(OutboxKey(database.Schema));
                if (failed > 0)
                {
                    alerts.Add(OutboxStuck(database.Schema, failed, settings.Outbox.MaxAttempts));
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The open alert of this schema stays as it is. The readiness check "database" reports the outage.
                logger.LogWarning(exception, "Alerts: the outbox of {Schema} could not be read", database.Schema);
                complete = false;
            }
        }

        if (string.IsNullOrWhiteSpace(settings.RabbitMq))
        {
            // The host does not use a broker.
            covered.Add(BrokerKey);
        }
        else if (broker.Known)
        {
            if (broker.DownSince is not { } since)
            {
                covered.Add(BrokerKey);
            }
            else if (context.Now - since >= settings.Broker.AlertAfter)
            {
                covered.Add(BrokerKey);
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(since, context.TimeZone).DateTime);
                alerts.Add(BrokerDown(day));
            }
            // A shorter outage is not covered: no new alert yet, and an alert from before a restart of the host stays
            // open until the connection is back.
        }

        // T99: the backups. Without a status folder, the host does not check them (and old alerts resolve).
        covered.Add(BackupPrefix);
        covered.Add(RestoreCheckPrefix);
        if (backups.Enabled)
        {
            alerts.AddRange(backups.Problems(context.Now).Select(problem => BackupAlert(problem, context.TimeZone)));
        }

        return new AlertCheckResult(alerts, covered, complete);
    }

    /// <summary>The alert of one backup problem. The time is a day in the time zone of the host.</summary>
    public static AlertCandidate BackupAlert(BackupProblem problem, TimeZoneInfo timeZone)
    {
        var values = new Dictionary<string, AlertValue> { ["name"] = AlertValue.Plain(problem.Name) };
        if (problem.Since is { } since)
        {
            values["date"] = AlertValue.Day(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(since, timeZone).DateTime));
        }
        if (!string.IsNullOrWhiteSpace(problem.Message))
        {
            values["message"] = AlertValue.Plain(problem.Message);
        }
        var key = (problem.Kind is BackupProblemKind.RestoreCheckFailed or BackupProblemKind.RestoreCheckOld ? RestoreCheckPrefix : BackupPrefix)
            + problem.Name.ToLowerInvariant();
        var (kind, title, text) = problem.Kind switch
        {
            BackupProblemKind.BackupFailed => ("backup-failed", "Backup failed",
                values.ContainsKey("message")
                    ? "The {name} backup of {date} failed: {message}. Check: docker compose logs backup"
                    : "The {name} backup of {date} failed. Check: docker compose logs backup"),
            BackupProblemKind.BackupOld => ("backup-old", "No recent backup",
                "The newest {name} backup is from {date}. The nightly backup did not run. Check: docker compose logs backup"),
            BackupProblemKind.RestoreCheckFailed => ("restore-check-failed", "Backup does not restore",
                values.ContainsKey("message")
                    ? "The restore check of the {name} backup failed on {date}: {message}"
                    : "The restore check of the {name} backup failed on {date}."),
            BackupProblemKind.RestoreCheckOld => ("restore-check-old", "No recent restore check",
                "The newest restore check of the {name} backup is from {date}. Check: docker compose logs backup"),
            _ => ("backup-missing", "No backup status",
                "Hestia has no backup status for {name}. The backup job did not run, or its status folder is not mounted.")
        };
        return new AlertCandidate(key, kind, title, text, values, AlertSeverity.Bad, Link: null, SortKey: $"0|{key}");
    }

    public static string OutboxKey(string schema) => OutboxPrefix + schema;

    public static AlertCandidate OutboxStuck(string schema, int count, int maxAttempts) =>
        new(OutboxKey(schema), "outbox-stuck", "Events are stuck",
            "{count} event(s) in the {schema} outbox failed {attempts} times. Hestia does not send them again. " +
            "To send them again, run: docker compose run --rm -T hestia-api retry-outbox {schema}",
            new Dictionary<string, AlertValue>
            {
                ["count"] = AlertValue.Count(count),
                ["schema"] = AlertValue.Plain(schema),
                ["attempts"] = AlertValue.Count(maxAttempts)
            },
            AlertSeverity.Bad, Link: null, SortKey: $"1|{schema}");

    public static AlertCandidate BrokerDown(DateOnly since) =>
        new(BrokerKey, "broker-down", "No event broker",
            "Hestia has no connection to the RabbitMQ broker since {date}, so it gets no finance events. Finance keeps " +
            "the events and sends them when the broker is back. Check the RabbitMQ container of finance.",
            new Dictionary<string, AlertValue> { ["date"] = AlertValue.Day(since) },
            AlertSeverity.Warning, Link: null, SortKey: "2");
}
