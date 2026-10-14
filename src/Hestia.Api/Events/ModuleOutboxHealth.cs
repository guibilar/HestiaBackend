using Hestia.Platform.Events;
using Hestia.Platform.Modules;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Hestia.Api.Events;

/// <summary>
/// The readiness check <c>events-outbox</c> (T37). <b>Degraded</b> (not Unhealthy: the apps still work) when a durable
/// module's outbox has a row that failed <c>Events:Outbox:MaxAttempts</c> times (the sender stopped trying it), or a
/// row older than <c>Events:Outbox:WarnAfter</c> (the sender is off, or it fails again and again). The description
/// names each schema and its counts. A database error is Degraded too; the check <c>database</c> reports the outage.
/// </summary>
public sealed class ModuleOutboxHealthCheck(
    IServiceScopeFactory scopes,
    IEnumerable<ModuleDatabase> databases,
    IOptionsMonitor<EventsOptions> options,
    TimeProvider time) : IHealthCheck
{
    public const string Name = "events-outbox";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.CurrentValue.Outbox;
        var oldBefore = time.GetUtcNow() - settings.WarnAfter;
        var problems = new List<string>();
        var data = new Dictionary<string, object>();
        await using var scope = scopes.CreateAsyncScope();
        foreach (var database in databases.Where(database => database.DurableEvents))
        {
            var outbox = ((DbContext)scope.ServiceProvider.GetRequiredService(database.ContextType)).Set<OutboxMessage>().AsNoTracking();
            var failed = await outbox.CountAsync(row => row.Attempts >= settings.MaxAttempts, cancellationToken);
            var old = await outbox.CountAsync(row => row.Attempts < settings.MaxAttempts && row.RaisedAt < oldBefore, cancellationToken);
            if (failed > 0)
            {
                problems.Add($"{database.Schema}: {failed} row(s) failed {settings.MaxAttempts} times (retry-outbox {database.Schema})");
                data[$"{database.Schema}.failed"] = failed;
            }
            if (old > 0)
            {
                problems.Add($"{database.Schema}: {old} row(s) older than {settings.WarnAfter}");
                data[$"{database.Schema}.old"] = old;
            }
        }
        return problems.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded($"Module outbox: {string.Join("; ", problems)}.", data: data);
    }
}

/// <summary>
/// <c>retry-outbox &lt;schema|all&gt;</c> (T37): gives the outbox rows that failed <c>Events:Outbox:MaxAttempts</c>
/// times a new start: <c>Attempts</c> 0 and <c>NextAttemptAt</c> now. The last error stays, for diagnostics. The
/// sender of the running host sends them at its next poll (<c>Events:Outbox:PollInterval</c>).
/// In Docker: <c>docker compose run --rm -T hestia-api retry-outbox all</c>.
/// </summary>
public sealed class RetryOutboxCommand : IModuleCommand
{
    public const string All = "all";

    public string Name => "retry-outbox";

    public string Description => "Sends the module outbox rows that failed too many times again: retry-outbox <schema|all>";

    public async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> arguments, TextReader input,
        TextWriter output, CancellationToken cancellationToken)
    {
        var durable = services.GetServices<ModuleDatabase>().Where(database => database.DurableEvents)
            .OrderBy(database => database.Schema, StringComparer.Ordinal).ToList();
        var schemas = string.Join(", ", durable.Select(database => database.Schema));
        if (arguments.Count != 1)
        {
            await output.WriteLineAsync($"Usage: {Name} <schema|{All}>. Modules with durable events: {schemas}.");
            return 1;
        }
        var selected = arguments[0] == All ? durable : durable.Where(database => database.Schema == arguments[0]).ToList();
        if (selected.Count == 0)
        {
            await output.WriteLineAsync($"No module with durable events has the schema \"{arguments[0]}\". " +
                $"Modules with durable events: {schemas}.");
            return 1;
        }

        var settings = services.GetRequiredService<IOptions<EventsOptions>>().Value.Outbox;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var total = 0;
        foreach (var database in selected)
        {
            await using var scope = services.CreateAsyncScope();
            var db = (DbContext)scope.ServiceProvider.GetRequiredService(database.ContextType);
            // Few rows, and the in-memory test database has no ExecuteUpdate: load them, change them, save once.
            var rows = await db.Set<OutboxMessage>().Where(row => row.Attempts >= settings.MaxAttempts).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                row.Attempts = 0;
                row.NextAttemptAt = now;
            }
            await db.SaveChangesAsync(cancellationToken);
            total += rows.Count;
            await output.WriteLineAsync($"{database.Schema}: {rows.Count} row(s) reset");
        }

        // The command runs in its own process in Docker; in the same process (tests), wake the sender at once.
        services.GetService<ModuleOutboxSignal>()?.Wake();
        await output.WriteLineAsync(total == 0
            ? "No failed rows."
            : $"Reset {total} row(s). The host sends them at its next outbox poll.");
        return 0;
    }
}
