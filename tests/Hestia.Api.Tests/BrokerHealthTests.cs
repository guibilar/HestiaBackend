using System.Diagnostics;
using System.Net;
using Hestia.Api.Events;
using Hestia.Contracts.Finance;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Hestia.Api.Tests;

/// <summary>
/// T37: the readiness check <c>events-broker</c> (Degraded while the host has no broker connection), and the reconnect
/// without a restart. A <see cref="BrokerGate"/> in front of the test broker plays a broker that is down, and one that
/// comes back. The tests with a real broker need HESTIA_TEST_RABBITMQ (CI uses "container").
/// </summary>
[Collection(BrokerCollection.Name)]
public class BrokerHealthTests
{
    private const string Check = BrokerHealthCheck.Name;

    /// <summary>A fast start and a fast reconnect, for the tests.</summary>
    private static Dictionary<string, string?> Settings(string rabbitMq) => new()
    {
        ["Events:RabbitMq"] = rabbitMq,
        ["Events:Broker:StartTimeout"] = "00:00:00",
        ["Events:Broker:ReconnectInterval"] = "00:00:00.500",
        ["Events:LogAll"] = "false"
    };

    [Fact]
    public async Task TheCheck_IsHealthy_WithoutABroker()
    {
        await using var factory = new HestiaApiFactory();
        factory.CreateClient();

        var result = await CheckAsync(factory);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("does not use a broker", result.Description);
    }

    [Fact]
    public async Task TheHost_StartsSoon_WhenTheBrokerIsDown_AndTheCheckIsDegraded_AndReadyStays200()
    {
        // Nothing listens on the port of a closed gate. No broker is needed.
        await using var gate = new BrokerGate("amqp://guest:guest@127.0.0.1:5672");
        await using var finance = await FakeFinance.StartAsync();
        var logs = new LogCapture();
        await using var factory = new HestiaApiFactory(finance.HostSettings(Settings(gate.Address)),
            services => services.AddSingleton<ILoggerProvider>(logs));
        var clock = Stopwatch.StartNew();

        var client = factory.CreateClient();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"The start took {clock.Elapsed}. {Log(logs)}");
        var result = await CheckAsync(factory);
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("no connection", result.Description);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Contains("Degraded", await ready.Content.ReadAsStringAsync());
        // The reconnector says so once.
        await Eventually(() => Task.FromResult(logs.Find("RabbitMQ broker is not reachable", LogLevel.Warning).Count), count => count > 0);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Single(logs.Find("RabbitMQ broker is not reachable", LogLevel.Warning));
    }

    [Fact]
    public async Task TheHost_ConnectsWhenTheBrokerComesBack_AndReceivesFinanceEvents_WithoutARestart()
    {
        Assert.SkipUnless(TestRabbitMq.Available, $"Needs RabbitMQ: set {TestRabbitMq.Variable}.");
        await using var gate = new BrokerGate(TestRabbitMq.Address!);
        var logs = new LogCapture();
        await using var factory = new HestiaApiFactory(Settings(gate.Address), services => services.AddSingleton<ILoggerProvider>(logs));
        factory.CreateClient();
        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(factory)).Status);

        gate.Open();

        var status = await Eventually(async () => (await CheckAsync(factory)).Status, s => s == HealthStatus.Healthy, seconds: 45);
        Assert.True(status == HealthStatus.Healthy, $"Still {status} after the broker came back. {Log(logs)}");
        Assert.NotEmpty(logs.Find("connected to the RabbitMQ broker after the start"));
        await AssertReceivesAsync(factory, logs);
    }

    [Fact]
    public async Task TheHost_RecoversWhenTheBrokerStopsAndComesBack()
    {
        Assert.SkipUnless(TestRabbitMq.Available, $"Needs RabbitMQ: set {TestRabbitMq.Variable}.");
        await using var gate = new BrokerGate(TestRabbitMq.Address!);
        gate.Open();
        var logs = new LogCapture();
        await using var factory = new HestiaApiFactory(Settings(gate.Address), services => services.AddSingleton<ILoggerProvider>(logs));
        factory.CreateClient();
        Assert.Equal(HealthStatus.Healthy, (await Eventually(async () => (await CheckAsync(factory)).Status, s => s == HealthStatus.Healthy)));

        gate.Close();

        var down = await Eventually(async () => (await CheckAsync(factory)).Status, s => s == HealthStatus.Degraded, seconds: 45);
        Assert.True(down == HealthStatus.Degraded, $"Still {down} after the broker stopped. {Log(logs)}");

        gate.Open();

        var up = await Eventually(async () => (await CheckAsync(factory)).Status, s => s == HealthStatus.Healthy, seconds: 60);
        Assert.True(up == HealthStatus.Healthy, $"Still {up} after the broker came back. {Log(logs)}");
        await AssertReceivesAsync(factory, logs);
    }

    /// <summary>Finance publishes an event to the broker (directly, not through the gate), and the host receives it.</summary>
    private static async Task AssertReceivesAsync(HestiaApiFactory factory, LogCapture logs)
    {
        using var finance = await FinanceEventTests.StartFinanceStandInAsync();
        var imported = new TransactionsImported(Guid.NewGuid(), "Chequing", 1, 1, 0,
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 13), DateTimeOffset.UtcNow);

        await FinanceEventTests.PublishAsync(finance, imported);

        try
        {
            Assert.Equal(imported, await factory.Events.WaitForAsync<TransactionsImported>(e => e.EventId == imported.EventId,
                TimeSpan.FromSeconds(30)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Assert.Fail($"The host did not receive the event. {Log(logs)}");
        }
    }

    private static async Task<HealthReportEntry> CheckAsync(HestiaApiFactory factory)
    {
        var report = await factory.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Name == Check);
        return report.Entries[Check];
    }

    /// <summary>The log lines about the broker, for a failure message (CI shows only the message).</summary>
    private static string Log(LogCapture logs) => "Log: " + string.Join(" | ", logs.Lines
        .Where(line => line.Level >= LogLevel.Information &&
                       (line.Category.Contains("Rabbit", StringComparison.Ordinal) || line.Message.Contains("Events:", StringComparison.Ordinal) ||
                        line.Message.Contains("broker", StringComparison.OrdinalIgnoreCase) || line.Message.Contains("listen", StringComparison.OrdinalIgnoreCase)))
        .TakeLast(12)
        .Select(line => $"{line.Level} {line.Category}: {Short(line.Message)}"));

    private static string Short(string text) => text.Length > 200 ? text[..200] : text;

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> done, int seconds = 30)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var value = await read();
            if (done(value) || DateTimeOffset.UtcNow > deadline)
            {
                return value;
            }
            await Task.Delay(100);
        }
    }
}
