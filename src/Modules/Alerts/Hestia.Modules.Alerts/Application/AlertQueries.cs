using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Alerts.Domain;
using Hestia.Modules.Alerts.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Alerts.Application;

/// <summary>The open alerts, for Today and for <c>GET /api/alerts</c> (<see cref="IAlertQueries"/>).</summary>
public sealed class AlertQueries(
    AlertsRunner runner,
    IServiceScopeFactory scopes,
    IOptions<AlertsOptions> options,
    ILogger<AlertQueries> logger) : IAlertQueries
{
    public async Task<AlertList> GetOpenAsync(AlertListOptions request, CancellationToken cancellationToken = default)
    {
        var sources = runner.Sources;

        // Fresh alerts on request, and while a source has no complete check since the host started (the first request
        // after a start, or after finance was down): the same freshness as when Today read finance itself.
        List<Task> waits = request.Refresh
            ? [runner.RequestAsync(null)]
            : sources.Where(s => runner.StateOf(s.Name)?.Last != AlertModuleStatus.Complete)
                .Select(s => runner.RequestAsync(s.Name))
                .ToList();
        if (waits.Count > 0)
        {
            try
            {
                await Task.WhenAll(waits).WaitAsync(options.Value.RefreshWait, cancellationToken);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Alerts: the check did not finish in {Wait}; the answer has the stored alerts", options.Value.RefreshWait);
            }
        }

        List<Alert> rows;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlertsDbContext>();
            rows = await db.Alerts.AsNoTracking().Where(a => a.ResolvedAt == null).ToListAsync(cancellationToken);
        }

        // Only the alerts of the registered sources: a source that is gone (renamed or removed) shows nothing.
        var orderOf = sources.ToDictionary(s => s.Name, s => s.Order, StringComparer.Ordinal);
        var known = rows.Where(a => orderOf.ContainsKey(a.Source)).ToList();
        IEnumerable<Alert> ordered = request.Order == AlertOrder.Module
            ? known.OrderBy(a => orderOf[a.Source])
                .ThenBy(a => a.Module, StringComparer.Ordinal)
                .ThenBy(a => a.SortKey, StringComparer.Ordinal)
                .ThenBy(a => a.Key, StringComparer.Ordinal)
            : known.OrderByDescending(a => a.Severity)
                .ThenByDescending(a => a.RaisedAt)
                .ThenBy(a => orderOf[a.Source])
                .ThenBy(a => a.SortKey, StringComparer.Ordinal)
                .ThenBy(a => a.Key, StringComparer.Ordinal);
        var alerts = ordered.Select(ToDto).ToList();

        var modules = sources
            .GroupBy(s => s.Module, StringComparer.Ordinal)
            .Select(group => new AlertModuleDto(group.Key, group.Min(s => s.Order),
                ModuleStatus(group.Select(s => runner.StateOf(s.Name)).ToList(), alerts.Any(a => a.Module == group.Key))))
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Module, StringComparer.Ordinal)
            .ToList();
        return new AlertList(runner.CheckedAll, modules, alerts);
    }

    /// <summary>
    /// The status of a module from the last check of each of its sources. A module whose checks all failed, but that has
    /// stored alerts, is Partial: its alerts show, but they can be old.
    /// </summary>
    public static AlertModuleStatus ModuleStatus(IReadOnlyList<AlertSourceState?> states, bool hasAlerts)
    {
        var each = states.Select(state => state switch
        {
            null => AlertModuleStatus.Failed,
            { Last: AlertModuleStatus.Failed, Worked: true } => AlertModuleStatus.Partial,
            { } known => known.Last
        }).ToList();
        if (each.All(status => status == AlertModuleStatus.Complete))
        {
            return AlertModuleStatus.Complete;
        }
        if (each.All(status => status == AlertModuleStatus.Failed) && !hasAlerts)
        {
            return AlertModuleStatus.Failed;
        }
        return AlertModuleStatus.Partial;
    }

    public static AlertDto ToDto(Alert alert) =>
        new(alert.Key, alert.Id, alert.Module, alert.Kind, alert.Title, alert.Text, AlertValuesJson.Read(alert.Values),
            alert.Severity, alert.Link, alert.RaisedAt, alert.UpdatedAt);
}
