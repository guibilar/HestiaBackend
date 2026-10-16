using System.Diagnostics;
using Hestia.Modules.Calendar.Contracts;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Finance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Calendar.Application;

/// <summary>
/// The Calendar of a range (T103): it calls every <see cref="ICalendarSource"/> (the events of this module, and the dated
/// things of the other modules) at the same time, and puts the items in order. A source that fails or does not answer in
/// time gives no items, and the answer marks its module as failed.
/// </summary>
public sealed class CalendarAgenda(
    IEnumerable<ICalendarSource> sources,
    CalendarDays days,
    IOptions<CalendarOptions> options,
    ILogger<CalendarAgenda> logger) : ICalendarQueries
{
    private readonly CalendarOptions _options = options.Value;

    public DateOnly Today => days.Today;

    /// <summary>The range of <c>GET /api/calendar</c>: from today for 31 days by default.</summary>
    public Task<CalendarDto> GetAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var start = from ?? days.Today;
        var end = to ?? start.AddDays(30);
        if (end < start)
        {
            throw new InvalidInputException("to", "to cannot be before from.");
        }
        if (end.DayNumber - start.DayNumber + 1 > CalendarLimits.MaxRangeDays)
        {
            throw new InvalidInputException("to", $"One answer can have at most {CalendarLimits.MaxRangeDays} days.");
        }
        return GetAsync(start, end, cancellationToken);
    }

    public async Task<CalendarDto> GetAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var range = new CalendarRange(from, to, days.TimeZone);
        var results = await Task.WhenAll(sources.Select(source => RunAsync(source, range, cancellationToken)));
        var modules = results.GroupBy(result => result.Module, StringComparer.Ordinal)
            .Select(group => new CalendarModuleDto(group.Key, Status(group.Select(r => r.Status).ToList())))
            .OrderBy(m => m.Module, StringComparer.Ordinal)
            .ToList();
        var items = Order(results.SelectMany(result => result.Items)
            .Where(item => item.StartDate <= range.To && item.EndDate >= range.From)
            .DistinctBy(item => item.Id)).ToList();
        return new CalendarDto(days.Today, from, to, range.TimeZone.Id, items, modules);
    }

    /// <summary>By the first day; on a day, the things of the whole day first (by module and title), then by time.</summary>
    public static IEnumerable<CalendarItem> Order(IEnumerable<CalendarItem> items) =>
        items.OrderBy(i => i.StartDate)
            .ThenBy(i => i.AllDay ? 0 : 1)
            .ThenBy(i => i.StartsAt)
            .ThenBy(i => i.Module == "Calendar" ? 0 : 1)
            .ThenBy(i => i.Module, StringComparer.Ordinal)
            .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.Id, StringComparer.Ordinal);

    private static CalendarModuleStatus Status(IReadOnlyList<CalendarModuleStatus> parts) =>
        parts.All(p => p == CalendarModuleStatus.Complete) ? CalendarModuleStatus.Complete
        : parts.All(p => p == CalendarModuleStatus.Failed) ? CalendarModuleStatus.Failed
        : CalendarModuleStatus.Partial;

    private sealed record SourceResult(string Module, CalendarModuleStatus Status, IReadOnlyList<CalendarItem> Items);

    private async Task<SourceResult> RunAsync(ICalendarSource source, CalendarRange range, CancellationToken requestAborted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        timeout.CancelAfter(_options.SourceTimeout);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await source.ListAsync(range, timeout.Token);
            // A source must give items of its own module only.
            var items = result.Items.Where(item => item.Module == source.Module).ToList();
            if (items.Count != result.Items.Count)
            {
                logger.LogWarning("Calendar: the {Module} source gave items of another module; they are left out", source.Module);
            }
            return new SourceResult(source.Module, result.Complete ? CalendarModuleStatus.Complete : CalendarModuleStatus.Partial, items);
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Calendar: the {Module} source did not answer in {Timeout}", source.Module, _options.SourceTimeout);
            return new SourceResult(source.Module, CalendarModuleStatus.Failed, []);
        }
        catch (FinanceRequestException exception)
        {
            logger.LogWarning("Calendar: the {Module} source could not reach finance: {Message}", source.Module, exception.Message);
            return new SourceResult(source.Module, CalendarModuleStatus.Failed, []);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Calendar: the {Module} source failed after {Elapsed} ms", source.Module,
                (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new SourceResult(source.Module, CalendarModuleStatus.Failed, []);
        }
    }
}
