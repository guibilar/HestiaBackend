using System.Diagnostics;
using System.Security.Claims;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Today.Contracts;
using Hestia.Platform.Auth;
using Hestia.Platform.Finance;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Today.Application;

/// <summary>
/// Makes the Today answer: it calls every <see cref="ITodayCardProvider"/> and reads the open alerts
/// (<see cref="IAlertQueries"/>, T15) at the same time, and puts the cards in module order. The alerts of a module are its
/// "Needs attention" items. A provider that fails or does not answer in time gives no cards, and the answer marks its
/// module as failed. A complete answer stays in a cache for each user for a short time.
/// </summary>
public sealed class TodayService(
    IEnumerable<ITodayCardProvider> providers,
    IAlertQueries alerts,
    IMemoryCache cache,
    IOptions<TodayOptions> options,
    TimeProvider time,
    ILogger<TodayService> logger)
{
    private readonly TodayOptions _options = options.Value;

    public async Task<TodayDto> GetAsync(ClaimsPrincipal user, bool refresh, CancellationToken cancellationToken)
    {
        var timeZone = _options.GetTimeZone();
        var now = time.GetUtcNow();
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);

        // Per user and scope: a read-only user never gets the answer of a full user from the cache, and the reverse.
        var key = $"today:{user.FindFirstValue(ClaimTypes.NameIdentifier)}:{HestiaUser.ScopeOf(user)}:{date:yyyy-MM-dd}";
        if (!refresh && cache.TryGetValue(key, out TodayDto? cached) && cached is not null)
        {
            return cached;
        }

        var context = new TodayContext(date, timeZone, user);
        var fromProviders = Task.WhenAll(providers.Select(provider => RunAsync(provider, context, cancellationToken)));
        var fromAlerts = RunAlertsAsync(refresh, cancellationToken);
        await Task.WhenAll(fromProviders, fromAlerts);

        // One entry per module: its provider and its alerts together.
        var modules = fromProviders.Result.Concat(fromAlerts.Result)
            .GroupBy(part => part.Module, StringComparer.Ordinal)
            .Select(group => new ModuleResult(group.Key, group.Min(part => part.Order), Status(group.ToList()),
                group.SelectMany(part => part.Cards).ToList()))
            .OrderBy(module => module.Order)
            .ThenBy(module => module.Module, StringComparer.Ordinal)
            .ToList();

        var cards = modules.SelectMany(m => m.Cards).Where(card => card.Type == TodayCardType.Tile)
            .Concat(modules.SelectMany(m => m.Cards).Where(card => card.Type == TodayCardType.Attention))
            .ToList();
        var answer = new TodayDto(date, now, modules.Select(m => new TodayModuleDto(m.Module, m.Status)).ToList(), cards);

        // Keep only a complete answer: after a failure, the next request tries again.
        if (modules.All(m => m.Status == TodayModuleStatus.Complete) && _options.CacheDuration > TimeSpan.Zero)
        {
            cache.Set(key, answer, _options.CacheDuration);
        }
        return answer;
    }

    /// <summary>One part of a module on Today: its provider, or its alerts.</summary>
    private sealed record PartResult(string Module, int Order, TodayModuleStatus Status, IReadOnlyList<TodayCard> Cards);

    private sealed record ModuleResult(string Module, int Order, TodayModuleStatus Status, IReadOnlyList<TodayCard> Cards);

    /// <summary>
    /// The status of a module from its parts: Complete when all parts are, Failed when all parts failed (then it has no
    /// cards), else Partial.
    /// </summary>
    private static TodayModuleStatus Status(IReadOnlyList<PartResult> parts) =>
        parts.All(part => part.Status == TodayModuleStatus.Complete) ? TodayModuleStatus.Complete
        : parts.All(part => part.Status == TodayModuleStatus.Failed) ? TodayModuleStatus.Failed
        : TodayModuleStatus.Partial;

    private async Task<PartResult> RunAsync(ITodayCardProvider provider, TodayContext context, CancellationToken requestAborted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        timeout.CancelAfter(_options.ProviderTimeout);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await provider.GetCardsAsync(context, timeout.Token);
            // A provider must give cards of its own module only.
            var cards = result.Cards.Where(card => card.Module == provider.Module).ToList();
            if (cards.Count != result.Cards.Count)
            {
                logger.LogWarning("Today: the {Module} provider gave cards of another module; they are left out", provider.Module);
            }
            return new PartResult(provider.Module, provider.Order,
                result.Complete ? TodayModuleStatus.Complete : TodayModuleStatus.Partial, cards);
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Today: the {Module} provider did not answer in {Timeout}", provider.Module, _options.ProviderTimeout);
            return new PartResult(provider.Module, provider.Order, TodayModuleStatus.Failed, []);
        }
        catch (FinanceRequestException exception)
        {
            // Finance is down: expected from time to time, and the finance client logged the details.
            logger.LogWarning("Today: the {Module} provider could not reach finance: {Message}", provider.Module, exception.Message);
            return new PartResult(provider.Module, provider.Order, TodayModuleStatus.Failed, []);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Today: the {Module} provider failed after {Elapsed} ms", provider.Module,
                (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new PartResult(provider.Module, provider.Order, TodayModuleStatus.Failed, []);
        }
    }

    /// <summary>
    /// The alerts of each module, as "Needs attention" cards, in the order of each module's rules (T15). With
    /// <paramref name="refresh"/> (pull to refresh), the Alerts module checks the rules first.
    /// </summary>
    private async Task<IReadOnlyList<PartResult>> RunAlertsAsync(bool refresh, CancellationToken requestAborted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        timeout.CancelAfter(_options.ProviderTimeout);
        try
        {
            var list = await alerts.GetOpenAsync(new AlertListOptions(refresh, AlertOrder.Module), timeout.Token);
            return list.Modules
                .Select(module => new PartResult(module.Module, module.Order, AlertCards.Status(module.Status),
                    list.Alerts.Where(alert => alert.Module == module.Module).Select(AlertCards.Card).ToList()))
                .ToList();
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Today: the alerts did not load");
            return [];
        }
    }
}

/// <summary>The settings of the Today module (section <c>Today</c>).</summary>
public sealed class TodayOptions
{
    public const string SectionName = "Today";

    /// <summary>
    /// IANA time zone (for example America/Edmonton) of this module. Without a value, the shared Hestia:TimeZone (T85,
    /// Docker: TZ) applies; set it here only to override that.
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>How long the host keeps a complete answer for a user. Zero turns the cache off.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the host waits for one provider. After it, that module is "Failed".</summary>
    public TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public TimeZoneInfo GetTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>True when the settings can work. The host checks this at start.</summary>
    public bool IsValid()
    {
        if (CacheDuration < TimeSpan.Zero || ProviderTimeout <= TimeSpan.Zero)
        {
            return false;
        }
        try
        {
            GetTimeZone();
            return true;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
