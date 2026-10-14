using System.Net;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Alerts.Domain;
using Hestia.Modules.Alerts.Infrastructure;
using Hestia.Platform.Events;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;

namespace Hestia.Modules.Alerts.Tests;

/// <summary>A clock that a test can move.</summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>The host with all modules, finance from <see cref="FakeFinance"/>, and only the finance alert source.</summary>
public static class AlertsHost
{
    public const string Alerts = "/api/alerts";

    public static HestiaApiFactory Start(FakeFinance finance, TimeProvider? clock = null,
        Dictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["Alerts:TimeZone"] = FinanceData.TimeZone,
            ["Today:TimeZone"] = FinanceData.TimeZone
        };
        foreach (var (key, value) in settings ?? [])
        {
            all[key] = value;
        }
        return new HestiaApiFactory(finance.HostSettings(all), configure =>
        {
            // The sources of other modules (Documents) have their own tests.
            foreach (var source in configure.Where(service => service.ServiceType == typeof(IAlertSource) &&
                         service.ImplementationType?.Namespace?.StartsWith("Hestia.Modules.Alerts.", StringComparison.Ordinal) == false).ToList())
            {
                configure.Remove(source);
            }
            if (clock is not null)
            {
                configure.RemoveAll<TimeProvider>();
                configure.AddSingleton(clock);
            }
            services?.Invoke(configure);
        });
    }

    public static async Task<AlertList> GetAlertsAsync(HttpClient client, bool refresh = false)
    {
        var response = await client.GetAsync(refresh ? $"{Alerts}?refresh=true" : Alerts);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<AlertList>();
    }

    /// <summary>All alert rows (open and resolved), oldest first.</summary>
    public static Task<List<Alert>> RowsAsync(HestiaApiFactory factory) =>
        factory.WithScopeAsync(services => services.GetRequiredService<AlertsDbContext>().Alerts.AsNoTracking()
            .OrderBy(a => a.RaisedAt).ThenBy(a => a.Id).ToListAsync());

    public static Task<int> InboxRowsAsync(HestiaApiFactory factory, Guid eventId, string consumer) =>
        factory.WithScopeAsync(services => services.GetRequiredService<AlertsDbContext>().Set<InboxEntry>()
            .CountAsync(entry => entry.EventId == eventId && entry.Consumer == consumer));

    /// <summary>A delivery through Wolverine: all consumers of the event run, and the call waits for them.</summary>
    public static async Task DeliverAsync(HestiaApiFactory factory, object message)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message);
    }

    /// <summary>Waits until <paramref name="condition"/> holds. Fails after the timeout.</summary>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited in vain: {what}.");
            }
            await Task.Delay(100);
        }
    }
}
