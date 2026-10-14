using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Alerts.Application;

/// <summary>
/// The background checks of the alert rules (T15). It runs each requested check (Today, <c>GET /api/alerts</c>, finance
/// events), and checks all sources every <c>Alerts:CheckInterval</c>. It runs outside any HTTP request, so the finance
/// source can call finance as the system identity. The first timed check comes one interval after the start: the first
/// request for alerts asks for a check itself.
/// </summary>
public sealed class AlertsWorker(AlertsRunner runner, IOptions<AlertsOptions> options, ILogger<AlertsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var interval = options.Value.CheckInterval;
        var nextTimedCheck = Stopwatch.GetTimestamp() + Ticks(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            var left = TimeSpan.FromSeconds(Math.Max(0, nextTimedCheck - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
            bool requested;
            try
            {
                requested = await runner.WaitForRequestAsync(left, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var timed = !requested || Stopwatch.GetTimestamp() >= nextTimedCheck;
            if (timed)
            {
                // A check of all sources, without a waiting request. The task ends with that check; nobody waits for it.
                _ = runner.RequestAsync(null);
                nextTimedCheck = Stopwatch.GetTimestamp() + Ticks(interval);
            }

            try
            {
                await runner.RunRequestedAsync(stoppingToken);
                if (timed)
                {
                    await runner.PurgeAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Alerts: the background check failed. It runs again at the next request or in {Interval}", interval);
            }
        }
    }

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);
}
