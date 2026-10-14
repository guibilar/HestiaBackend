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
    ILogger<SystemHealthAlerts> logger) : IAlertSource
{
    public const string SourceName = "System.Health";
    public const string ModuleName = "System";
    public const string OutboxPrefix = "system.outbox-stuck.";
    public const string BrokerKey = "system.broker-down";

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

        return new AlertCheckResult(alerts, covered, complete);
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
