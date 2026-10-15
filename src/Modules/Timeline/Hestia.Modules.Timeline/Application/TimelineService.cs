using System.Diagnostics;
using Hestia.Modules.Timeline.Contracts;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Finance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Timeline.Application;

/// <summary>
/// Makes one Timeline page (T16): it calls every <see cref="ITimelineSource"/> (or the one of a module) for the days of
/// the page at the same time, and groups their entries by day. A source that fails or does not answer in time gives no
/// entries, and the page marks its module as failed.
/// </summary>
public sealed class TimelineService(
    IEnumerable<ITimelineSource> sources,
    IOptions<TimelineOptions> options,
    TimeProvider time,
    ILogger<TimelineService> logger)
{
    private readonly TimelineOptions _options = options.Value;

    /// <param name="before">The day after the page (the page ends the day before it); null: the page ends today.</param>
    /// <param name="days">The days of the page, 1 to <see cref="TimelineOptions.MaxDays"/>.</param>
    /// <param name="module">Only this module (for example <c>Money</c>); null: all.</param>
    public async Task<TimelineDto> GetAsync(DateOnly? before, int days, string? module, CancellationToken cancellationToken)
    {
        if (days is < 1 or > TimelineOptions.MaxDays)
        {
            throw new InvalidInputException("days", $"days must be from 1 to {TimelineOptions.MaxDays}.");
        }
        var timeZone = _options.GetTimeZone();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), timeZone).DateTime);
        var end = before is { } b ? (b > today ? today : b.AddDays(-1)) : today;
        var start = end.AddDays(-(days - 1));
        var range = new TimelineRange(start, end, timeZone);

        var chosen = sources.ToList();
        if (!string.IsNullOrWhiteSpace(module))
        {
            chosen = chosen.Where(source => string.Equals(source.Module, module.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (chosen.Count == 0)
            {
                throw new InvalidInputException("module",
                    $"No module \"{module}\" has a Timeline. Modules: {string.Join(", ", sources.Select(s => s.Module).Distinct().Order())}.");
            }
        }

        var results = await Task.WhenAll(chosen.Select(source => RunAsync(source, range, cancellationToken)));

        var modules = results.GroupBy(result => result.Module, StringComparer.Ordinal)
            .Select(group => new TimelineModuleDto(group.Key, Status(group.Select(r => r.Status).ToList())))
            .OrderBy(m => m.Module, StringComparer.Ordinal)
            .ToList();
        var dayList = results.SelectMany(result => result.Entries)
            .Where(entry => range.Contains(entry.Day))
            .GroupBy(entry => entry.Day)
            .OrderByDescending(group => group.Key)
            .Select(group => new TimelineDayDto(group.Key, Order(group).ToList()))
            .ToList();
        return new TimelineDto(today, start, end, start, dayList, modules);
    }

    /// <summary>The entries of a day: those with a time, newest first; then those of the whole day, by module and title.</summary>
    private static IEnumerable<TimelineEntry> Order(IEnumerable<TimelineEntry> entries) =>
        entries.OrderBy(e => e.At is null ? 1 : 0)
            .ThenByDescending(e => e.At)
            .ThenBy(e => e.Module, StringComparer.Ordinal)
            .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.Ordinal);

    private static TimelineModuleStatus Status(IReadOnlyList<TimelineModuleStatus> parts) =>
        parts.All(p => p == TimelineModuleStatus.Complete) ? TimelineModuleStatus.Complete
        : parts.All(p => p == TimelineModuleStatus.Failed) ? TimelineModuleStatus.Failed
        : TimelineModuleStatus.Partial;

    private sealed record SourceResult(string Module, TimelineModuleStatus Status, IReadOnlyList<TimelineEntry> Entries);

    private async Task<SourceResult> RunAsync(ITimelineSource source, TimelineRange range, CancellationToken requestAborted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        timeout.CancelAfter(_options.SourceTimeout);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await source.ListAsync(range, timeout.Token);
            // A source must give entries of its own module only.
            var entries = result.Entries.Where(entry => entry.Module == source.Module).ToList();
            if (entries.Count != result.Entries.Count)
            {
                logger.LogWarning("Timeline: the {Module} source gave entries of another module; they are left out", source.Module);
            }
            return new SourceResult(source.Module, result.Complete ? TimelineModuleStatus.Complete : TimelineModuleStatus.Partial, entries);
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Timeline: the {Module} source did not answer in {Timeout}", source.Module, _options.SourceTimeout);
            return new SourceResult(source.Module, TimelineModuleStatus.Failed, []);
        }
        catch (FinanceRequestException exception)
        {
            logger.LogWarning("Timeline: the {Module} source could not reach finance: {Message}", source.Module, exception.Message);
            return new SourceResult(source.Module, TimelineModuleStatus.Failed, []);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Timeline: the {Module} source failed after {Elapsed} ms", source.Module,
                (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new SourceResult(source.Module, TimelineModuleStatus.Failed, []);
        }
    }
}
