using System.Diagnostics;
using System.Security.Claims;
using Hestia.Modules.Today.Contracts;
using Hestia.Platform.Auth;
using Hestia.Platform.Finance;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Today.Application;

/// <summary>
/// Makes the Today answer: it calls every <see cref="ITodayCardProvider"/> at the same time, and puts the cards in
/// module order. A provider that fails or does not answer in time gives no cards, and the answer marks its module as
/// failed. A complete answer stays in a cache for each user for a short time.
/// </summary>
public sealed class TodayService(
    IEnumerable<ITodayCardProvider> providers,
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
        var ordered = providers.OrderBy(p => p.Order).ThenBy(p => p.Module, StringComparer.Ordinal).ToList();
        var results = await Task.WhenAll(ordered.Select(provider => RunAsync(provider, context, cancellationToken)));

        var cards = results.SelectMany(r => r.Cards).Where(card => card.Type == TodayCardType.Tile)
            .Concat(results.SelectMany(r => r.Cards).Where(card => card.Type == TodayCardType.Attention))
            .ToList();
        var answer = new TodayDto(date, now, results.Select(r => new TodayModuleDto(r.Module, r.Status)).ToList(), cards);

        // Keep only a complete answer: after a failure, the next request tries again.
        if (results.All(r => r.Status == TodayModuleStatus.Complete) && _options.CacheDuration > TimeSpan.Zero)
        {
            cache.Set(key, answer, _options.CacheDuration);
        }
        return answer;
    }

    private sealed record ProviderResult(string Module, TodayModuleStatus Status, IReadOnlyList<TodayCard> Cards);

    private async Task<ProviderResult> RunAsync(ITodayCardProvider provider, TodayContext context, CancellationToken requestAborted)
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
            return new ProviderResult(provider.Module, result.Complete ? TodayModuleStatus.Complete : TodayModuleStatus.Partial, cards);
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Today: the {Module} provider did not answer in {Timeout}", provider.Module, _options.ProviderTimeout);
            return new ProviderResult(provider.Module, TodayModuleStatus.Failed, []);
        }
        catch (FinanceRequestException exception)
        {
            // Finance is down: expected from time to time, and the finance client logged the details.
            logger.LogWarning("Today: the {Module} provider could not reach finance: {Message}", provider.Module, exception.Message);
            return new ProviderResult(provider.Module, TodayModuleStatus.Failed, []);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Today: the {Module} provider failed after {Elapsed} ms", provider.Module,
                (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new ProviderResult(provider.Module, TodayModuleStatus.Failed, []);
        }
    }
}

/// <summary>The settings of the Today module (section <c>Today</c>).</summary>
public sealed class TodayOptions
{
    public const string SectionName = "Today";

    /// <summary>
    /// IANA time zone (for example America/Edmonton) that decides which day it is. Use the same zone as finance
    /// (<c>Reports:TimeZone</c>), so that "this month" is the same month. Docker sets it from <c>TZ</c>.
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
