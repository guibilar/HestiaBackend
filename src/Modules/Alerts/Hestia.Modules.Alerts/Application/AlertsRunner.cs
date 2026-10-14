using System.Collections.Concurrent;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Alerts.Domain;
using Hestia.Modules.Alerts.Infrastructure;
using Hestia.Platform.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Alerts.Application;

/// <summary>A registered alert source: its name, module, and place on Today.</summary>
public sealed record AlertSourceInfo(string Name, string Module, int Order);

/// <summary>The result of the last check of one source since the host started.</summary>
/// <param name="Last">Complete, Partial (some data did not load), or Failed (the check threw).</param>
/// <param name="CheckedAt">When that check finished.</param>
/// <param name="Worked">True when a check since the host started was Complete or Partial.</param>
public sealed record AlertSourceState(AlertModuleStatus Last, DateTimeOffset CheckedAt, bool Worked);

/// <summary>
/// Runs the alert sources and stores their results (T15). One instance for the host: it keeps the requests that wait for a
/// check, and the result of the last check of each source.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A check of a source runs alone (one lock per source), so two checks of the same source never write at the same
/// time. Checks of different sources run in parallel: their keys never overlap (each key starts with its module).</item>
/// <item>The checks of all sources (timer, refresh, finance events) run in the <see cref="AlertsWorker"/>, outside any
/// HTTP request, so a source can call finance as the system identity (<c>IFinanceClient.AsSystem</c>, T42).</item>
/// <item>A module's event consumer runs one source at once, with <see cref="IAlertChecks.CheckAsync"/>.</item>
/// </list>
/// </remarks>
public sealed class AlertsRunner(
    IServiceScopeFactory scopes,
    TimeProvider time,
    IOptions<AlertsOptions> options,
    ILogger<AlertsRunner> logger)
{
    /// <summary>The request key for "all sources".</summary>
    private const string All = "*";

    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AlertSourceState> _states = new(StringComparer.Ordinal);
    private IReadOnlyList<AlertSourceInfo>? _sources;
    private long _checkedAllTicks;

    /// <summary>When the last check of all sources finished, or null when none did since the host started.</summary>
    public DateTimeOffset? CheckedAll
    {
        get
        {
            var ticks = Interlocked.Read(ref _checkedAllTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>The registered sources, in module order. Read once: the registrations do not change while the host runs.</summary>
    public IReadOnlyList<AlertSourceInfo> Sources
    {
        get
        {
            if (_sources is { } known)
            {
                return known;
            }
            using var scope = scopes.CreateScope();
            var sources = new List<AlertSourceInfo>();
            foreach (var source in scope.ServiceProvider.GetServices<IAlertSource>())
            {
                if (string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > Alert.MaxSourceLength ||
                    string.IsNullOrWhiteSpace(source.Module) || source.Module.Length > Alert.MaxModuleLength)
                {
                    logger.LogError("Alerts: the source {Type} has no valid name or module; it is left out", source.GetType().FullName);
                    continue;
                }
                if (sources.Any(known => known.Name == source.Name))
                {
                    logger.LogError("Alerts: two sources have the name {Source}; the second ({Type}) is left out", source.Name,
                        source.GetType().FullName);
                    continue;
                }
                sources.Add(new AlertSourceInfo(source.Name, source.Module, source.Order));
            }
            _sources = sources.OrderBy(s => s.Order).ThenBy(s => s.Module, StringComparer.Ordinal)
                .ThenBy(s => s.Name, StringComparer.Ordinal).ToList();
            return _sources;
        }
    }

    /// <summary>The result of the last check of a source since the host started, or null.</summary>
    public AlertSourceState? StateOf(string source) => _states.TryGetValue(source, out var state) ? state : null;

    // ---- Requests (the worker runs them) ----------------------------------------------------------------------------

    /// <summary>
    /// Asks for a check of <paramref name="source"/> (null: all sources) in the background. The task ends when a check
    /// that started after this call has finished (also when it failed: see <see cref="StateOf"/>). Requests that come
    /// before the check starts share it.
    /// </summary>
    public Task RequestAsync(string? source)
    {
        var key = source ?? All;
        Task task;
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var waiting))
            {
                waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[key] = waiting;
            }
            task = waiting.Task;
        }
        Wake();
        return task;
    }

    /// <summary>Waits for a request, at most <paramref name="timeout"/>. True when a request came.</summary>
    public Task<bool> WaitForRequestAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _wake.WaitAsync(timeout, cancellationToken);

    private void Wake()
    {
        if (_wake.CurrentCount > 0)
        {
            return;
        }
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another request woke the worker at the same time.
        }
    }

    /// <summary>Runs the requested checks (the worker calls it). A source that fails does not stop the others.</summary>
    public async Task RunRequestedAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, TaskCompletionSource> batch;
        lock (_gate)
        {
            batch = new Dictionary<string, TaskCompletionSource>(_pending, StringComparer.Ordinal);
            _pending.Clear();
        }
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            var all = batch.ContainsKey(All);
            var known = Sources;
            var names = all ? known.Select(s => s.Name).ToList() : batch.Keys.Where(name => known.Any(s => s.Name == name)).ToList();
            foreach (var unknown in batch.Keys.Where(name => name != All && known.All(s => s.Name != name)))
            {
                logger.LogWarning("Alerts: a check was asked for the unknown source {Source}", unknown);
            }

            await Task.WhenAll(names.Select(name => CheckInBackgroundAsync(name, cancellationToken)));
            if (all || known.All(s => names.Contains(s.Name)))
            {
                Interlocked.Exchange(ref _checkedAllTicks, time.GetUtcNow().UtcTicks);
            }
        }
        finally
        {
            foreach (var waiting in batch.Values)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    waiting.TrySetCanceled(cancellationToken);
                }
                else
                {
                    waiting.TrySetResult();
                }
            }
        }
    }

    /// <summary>A check of all of one source, which never throws: a failure is in the log and in <see cref="StateOf"/>.</summary>
    private async Task CheckInBackgroundAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(name, target: null, trigger: null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host stops.
        }
        catch (FinanceRequestException exception)
        {
            // Finance is down: expected from time to time, and the finance client logged the details.
            logger.LogWarning("Alerts: the source {Source} could not reach finance: {Message}", name, exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Alerts: the check of the source {Source} failed", name);
        }
    }

    // ---- Checks ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one source now and stores its result. With <paramref name="trigger"/>, the inbox row and the result commit
    /// together, and an event that the consumer handled already changes nothing. A failure throws.
    /// </summary>
    /// <exception cref="ArgumentException">No source has this name.</exception>
    public async Task RunAsync(string name, string? target, AlertTrigger? trigger, CancellationToken cancellationToken)
    {
        var info = Sources.FirstOrDefault(s => s.Name == name)
            ?? throw new ArgumentException($"No alert source has the name \"{name}\".", nameof(name));
        var gate = _locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var source = scope.ServiceProvider.GetServices<IAlertSource>().First(s => s.Name == name);
            var db = scope.ServiceProvider.GetRequiredService<AlertsDbContext>();
            if (trigger is not null && !await db.TryReceiveAsync(trigger.EventId, trigger.Consumer, cancellationToken))
            {
                logger.LogDebug("Alerts: event {EventId} came again; {Consumer} handled it already", trigger.EventId, trigger.Consumer);
                return;
            }

            var now = time.GetUtcNow();
            var timeZone = options.Value.GetTimeZone();
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
            try
            {
                var result = await source.CheckAsync(new AlertCheckContext(date, timeZone, now, target), cancellationToken);
                await ApplyAsync(db, info, result, now, cancellationToken);
                if (target is null)
                {
                    SetState(name, result.Complete ? AlertModuleStatus.Complete : AlertModuleStatus.Partial, now);
                }
            }
            catch (Exception) when (target is null && !cancellationToken.IsCancellationRequested)
            {
                // Only a check of all of the source tells the status of its module.
                SetState(name, AlertModuleStatus.Failed, now);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Raises (or updates) one alert from an event, without a check: for example a budget that reached its limit. It
    /// resolves nothing; the next check of the source does that. The inbox makes it idempotent.
    /// </summary>
    public async Task RaiseAsync(AlertSourceInfo source, AlertCandidate alert, AlertTrigger trigger, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(source.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AlertsDbContext>();
            if (!await db.TryReceiveAsync(trigger.EventId, trigger.Consumer, cancellationToken))
            {
                logger.LogDebug("Alerts: event {EventId} came again; {Consumer} handled it already", trigger.EventId, trigger.Consumer);
                return;
            }
            await ApplyAsync(db, source, new AlertCheckResult([alert], Covered: []), time.GetUtcNow(), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private void SetState(string source, AlertModuleStatus status, DateTimeOffset now) =>
        _states.AddOrUpdate(source,
            _ => new AlertSourceState(status, now, status != AlertModuleStatus.Failed),
            (_, old) => new AlertSourceState(status, now, old.Worked || status != AlertModuleStatus.Failed));

    /// <summary>
    /// Stores a result: a new key raises an alert, a known key updates its fields when they changed, and an open alert
    /// whose key the result covers but does not have is resolved. One save.
    /// </summary>
    private async Task ApplyAsync(AlertsDbContext db, AlertSourceInfo source, AlertCheckResult result, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var open = await db.Alerts
            .Where(a => a.Source == source.Name && a.ResolvedAt == null)
            .ToListAsync(cancellationToken);
        var byKey = new Dictionary<string, Alert>(StringComparer.Ordinal);
        foreach (var alert in open.OrderBy(a => a.RaisedAt).ThenBy(a => a.Id))
        {
            if (!byKey.TryAdd(alert.Key, alert))
            {
                // Two open alerts with one key cannot happen in PostgreSQL (a unique index). Keep the oldest.
                alert.ResolvedAt = now;
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in result.Alerts)
        {
            if (Problem(source, candidate) is { } problem)
            {
                logger.LogWarning("Alerts: the source {Source} gave an alert that cannot be stored ({Key}): {Problem}",
                    source.Name, candidate.Key, problem);
                continue;
            }
            if (!seen.Add(candidate.Key))
            {
                logger.LogWarning("Alerts: the source {Source} gave the key {Key} twice; the first one counts", source.Name, candidate.Key);
                continue;
            }

            var values = AlertValuesJson.Write(candidate.Values);
            var sortKey = candidate.SortKey.Length > Alert.MaxSortKeyLength ? candidate.SortKey[..Alert.MaxSortKeyLength] : candidate.SortKey;
            if (byKey.TryGetValue(candidate.Key, out var existing))
            {
                if (existing.Kind != candidate.Kind || existing.Title != candidate.Title || existing.Text != candidate.Text ||
                    existing.Values != values || existing.Severity != candidate.Severity || existing.Link != candidate.Link ||
                    existing.SortKey != sortKey)
                {
                    existing.Kind = candidate.Kind;
                    existing.Title = candidate.Title;
                    existing.Text = candidate.Text;
                    existing.Values = values;
                    existing.Severity = candidate.Severity;
                    existing.Link = candidate.Link;
                    existing.SortKey = sortKey;
                    existing.UpdatedAt = now;
                }
                continue;
            }

            db.Alerts.Add(new Alert
            {
                Id = Guid.CreateVersion7(),
                Source = source.Name,
                Module = source.Module,
                Key = candidate.Key,
                Kind = candidate.Kind,
                Severity = candidate.Severity,
                Title = candidate.Title,
                Text = candidate.Text,
                Values = values,
                Link = candidate.Link,
                SortKey = sortKey,
                RaisedAt = now,
                UpdatedAt = now
            });
        }

        foreach (var alert in byKey.Values.Where(a => !seen.Contains(a.Key) &&
                     result.Covered.Any(prefix => a.Key.StartsWith(prefix, StringComparison.Ordinal))))
        {
            alert.ResolvedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Why an alert cannot be stored, or null.</summary>
    private static string? Problem(AlertSourceInfo source, AlertCandidate alert)
    {
        var prefix = source.Module.ToLowerInvariant() + ".";
        if (!alert.Key.StartsWith(prefix, StringComparison.Ordinal) || alert.Key.Length == prefix.Length)
        {
            return $"the key must start with \"{prefix}\"";
        }
        if (alert.Key.Length > Alert.MaxKeyLength)
        {
            return $"the key has more than {Alert.MaxKeyLength} characters";
        }
        if (string.IsNullOrWhiteSpace(alert.Kind) || alert.Kind.Length > Alert.MaxKindLength)
        {
            return $"the kind must have 1 to {Alert.MaxKindLength} characters";
        }
        if (string.IsNullOrWhiteSpace(alert.Title) || alert.Title.Length > Alert.MaxTitleLength)
        {
            return $"the title must have 1 to {Alert.MaxTitleLength} characters";
        }
        if (string.IsNullOrWhiteSpace(alert.Text) || alert.Text.Length > Alert.MaxTextLength)
        {
            return $"the text must have 1 to {Alert.MaxTextLength} characters";
        }
        if (alert.Link is { Length: > Alert.MaxLinkLength })
        {
            return $"the link has more than {Alert.MaxLinkLength} characters";
        }
        return null;
    }

    /// <summary>Deletes the alerts that were resolved more than <c>Alerts:KeepResolvedDays</c> ago.</summary>
    public async Task PurgeAsync(CancellationToken cancellationToken)
    {
        var before = time.GetUtcNow().AddDays(-options.Value.KeepResolvedDays);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AlertsDbContext>();
        var old = await db.Alerts.Where(a => a.ResolvedAt != null && a.ResolvedAt < before).ToListAsync(cancellationToken);
        if (old.Count == 0)
        {
            return;
        }
        db.Alerts.RemoveRange(old);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Alerts: deleted {Count} alerts that were resolved before {Before}", old.Count, before);
    }
}
