using System.Text.Json;
using Hestia.Api.Events;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Sample;
using Hestia.Modules.Sample.Contracts;
using Hestia.Modules.Sample.Infrastructure;
using Hestia.Platform.Events;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hestia.Api.Tests;

/// <summary>
/// T80: the alerts of the host itself (module <c>System</c>): a stuck module outbox, and a long broker outage. The other
/// alert sources are removed, so the tests do not need finance.
/// </summary>
[Collection(BrokerCollection.Name)]
public class SystemAlertTests
{
    /// <summary>A clock that a test can move.</summary>
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);

    private static HestiaApiFactory Host(Dictionary<string, string?> settings, TimeProvider? clock = null) =>
        new(settings, services =>
        {
            foreach (var source in services.Where(s => s.ServiceType == typeof(IAlertSource) &&
                         s.ImplementationType != typeof(SystemHealthAlerts)).ToList())
            {
                services.Remove(source);
            }
            if (clock is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
            }
        });

    // --- system.outbox-stuck.<schema> ---

    [Fact]
    public async Task AStuckOutboxRow_RaisesABadAlert_WithTheFix_AndItResolves_WhenTheRowCanGoAgain()
    {
        await using var factory = Host(new Dictionary<string, string?>
        {
            [SampleModuleExtensions.DurableEventsSetting] = "true",
            // The sender is off, so the test controls the row.
            ["Events:Outbox:Enabled"] = "false",
            ["Alerts:TimeZone"] = "America/Edmonton"
        });
        var client = factory.CreateClient();
        var row = await AddRowAsync(factory, attempts: 10);
        await AddRowAsync(factory, attempts: 3);

        await CheckAsync(factory);
        var list = await AlertsAsync(client);

        var alert = Assert.Single(list.Alerts, a => a.Id == "system.outbox-stuck.sample");
        Assert.Equal(("System", "outbox-stuck", AlertSeverity.Bad, (string?)null), (alert.Module, alert.Kind, alert.Severity, alert.Link));
        Assert.Contains("retry-outbox {schema}", alert.Text);
        // Only the row at MaxAttempts counts.
        Assert.Equal(AlertValue.Count(1), alert.Values["count"]);
        Assert.Equal(AlertValue.Plain("sample"), alert.Values["schema"]);
        Assert.Equal(AlertValue.Count(10), alert.Values["attempts"]);
        Assert.Equal(new AlertModuleDto("System", 900, AlertModuleStatus.Complete), Assert.Single(list.Modules));
        // No broker in this host, so no broker alert.
        Assert.DoesNotContain(list.Alerts, a => a.Id == SystemHealthAlerts.BrokerKey);

        // What retry-outbox does: the row can go again.
        await SetAttemptsAsync(factory, row, 0);
        await CheckAsync(factory);

        Assert.DoesNotContain((await AlertsAsync(client)).Alerts, a => a.Module == "System");
    }

    [Fact]
    public async Task AnEmptyOutbox_GivesNoAlert()
    {
        await using var factory = Host(new Dictionary<string, string?> { [SampleModuleExtensions.DurableEventsSetting] = "true" });
        var client = factory.CreateClient();

        var list = await AlertsAsync(client, refresh: true);

        Assert.DoesNotContain(list.Alerts, a => a.Module == "System");
        Assert.Equal(AlertModuleStatus.Complete, Assert.Single(list.Modules, m => m.Module == "System").Status);
    }

    // --- system.broker-down ---

    [Fact]
    public void TheState_KeepsTheFirstTimeDown_UntilTheConnectionIsBack()
    {
        var clock = new TestClock(Start);
        var state = new BrokerConnectionState(clock);
        Assert.False(state.Known);

        state.Report(connected: false);
        clock.Now = Start.AddMinutes(5);
        state.Report(connected: false);

        Assert.True(state.Known);
        Assert.Equal(Start, state.DownSince);

        state.Report(connected: true);
        Assert.Null(state.DownSince);
        clock.Now = Start.AddMinutes(9);
        state.Report(connected: false);
        Assert.Equal(Start.AddMinutes(9), state.DownSince);
    }

    [Fact]
    public async Task ABrokerOutage_RaisesAWarning_OnlyAfter30Minutes_AndItResolves_WhenTheConnectionIsBack()
    {
        // Nothing listens on the port of a closed gate: the broker is down. No broker is needed.
        await using var gate = new BrokerGate("amqp://guest:guest@127.0.0.1:5672");
        var clock = new TestClock(Start);
        await using var factory = Host(new Dictionary<string, string?>
        {
            ["Events:RabbitMq"] = gate.Address,
            ["Events:Broker:StartTimeout"] = "00:00:00",
            // One report at the start; then the test reports itself.
            ["Events:Broker:ReconnectInterval"] = "01:00:00",
            ["Events:LogAll"] = "false",
            ["Alerts:TimeZone"] = "America/Edmonton"
        }, clock);
        var client = factory.CreateClient();
        var state = factory.Services.GetRequiredService<BrokerConnectionState>();
        await EventuallyAsync(() => state.Known, "the reconnector reports the state");
        Assert.Equal(Start, state.DownSince);

        clock.Now = Start.AddMinutes(29);
        await CheckAsync(factory);
        Assert.DoesNotContain((await AlertsAsync(client)).Alerts, a => a.Id == SystemHealthAlerts.BrokerKey);

        clock.Now = Start.AddMinutes(31);
        await CheckAsync(factory);
        var alert = Assert.Single((await AlertsAsync(client)).Alerts, a => a.Id == SystemHealthAlerts.BrokerKey);
        Assert.Equal(("System", "broker-down", AlertSeverity.Warning, (string?)null), (alert.Module, alert.Kind, alert.Severity, alert.Link));
        // 15:00 UTC is 09:00 in Edmonton, on the same day.
        Assert.Equal(AlertValue.Day(new DateOnly(2026, 10, 14)), alert.Values["date"]);

        // As after a restart of the host: a new outage of less than 30 minutes keeps the open alert.
        state.Report(connected: true);
        state.Report(connected: false);
        clock.Now = Start.AddMinutes(40);
        await CheckAsync(factory);
        Assert.Equal(alert.AlertId, Assert.Single((await AlertsAsync(client)).Alerts, a => a.Id == SystemHealthAlerts.BrokerKey).AlertId);

        state.Report(connected: true);
        await CheckAsync(factory);
        Assert.DoesNotContain((await AlertsAsync(client)).Alerts, a => a.Id == SystemHealthAlerts.BrokerKey);
    }

    [Fact]
    public async Task TheReconnector_ReportsUp_WhenTheBrokerComesBack()
    {
        Assert.SkipUnless(TestRabbitMq.Available, $"Needs RabbitMQ: set {TestRabbitMq.Variable}.");
        await using var gate = new BrokerGate(TestRabbitMq.Address!);
        await using var factory = Host(new Dictionary<string, string?>
        {
            ["Events:RabbitMq"] = gate.Address,
            ["Events:Broker:StartTimeout"] = "00:00:00",
            ["Events:Broker:ReconnectInterval"] = "00:00:00.500",
            ["Events:LogAll"] = "false"
        });
        factory.CreateClient();
        var state = factory.Services.GetRequiredService<BrokerConnectionState>();
        await EventuallyAsync(() => state.DownSince is not null, "the reconnector reports the broker down");

        gate.Open();

        await EventuallyAsync(() => state.DownSince is null, "the reconnector reports the broker up", TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task TheHost_DoesNotStart_WithAZeroAlertAfter()
    {
        await using var factory = Host(new Dictionary<string, string?> { ["Events:Broker:AlertAfter"] = "00:00:00" });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("AlertAfter", error.ToString());
    }

    // --- Helpers ---

    private static Task CheckAsync(HestiaApiFactory factory) =>
        factory.Services.GetRequiredService<IAlertChecks>().CheckAsync(SystemHealthAlerts.SourceName);

    private static async Task<AlertList> AlertsAsync(HttpClient client, bool refresh = false) =>
        await (await client.GetAsync(refresh ? "/api/alerts?refresh=true" : "/api/alerts")).ReadAsync<AlertList>();

    private static Task<long> AddRowAsync(HestiaApiFactory factory, int attempts) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<SampleDbContext>();
            var @event = new SampleItemCreated(Guid.NewGuid(), "Kettle", DateTimeOffset.UtcNow);
            var row = new OutboxMessage
            {
                EventType = typeof(SampleItemCreated).FullName!,
                Payload = JsonSerializer.Serialize(@event),
                RaisedAt = DateTimeOffset.UtcNow,
                Attempts = attempts,
                NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1),
                LastError = "It failed."
            };
            db.Set<OutboxMessage>().Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        });

    private static Task<int> SetAttemptsAsync(HestiaApiFactory factory, long id, int attempts) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<SampleDbContext>();
            var row = await db.Set<OutboxMessage>().SingleAsync(r => r.Id == id);
            row.Attempts = attempts;
            return await db.SaveChangesAsync();
        });

    private static async Task EventuallyAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited in vain: {what}.");
            }
            await Task.Delay(50);
        }
    }
}
