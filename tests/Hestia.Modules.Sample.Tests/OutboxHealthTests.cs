using System.Net;
using System.Text.Json;
using Hestia.Modules.Sample.Application;
using Hestia.Modules.Sample.Contracts;
using Hestia.Modules.Sample.Infrastructure;
using Hestia.Platform.Events;
using Hestia.Platform.Modules;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hestia.Modules.Sample.Tests;

/// <summary>
/// T37: the readiness check <c>events-outbox</c> (Degraded for a stuck or an old outbox row), and the command
/// <c>retry-outbox</c>, on the outbox of the Sample module (with <c>Sample:DurableEvents</c>).
/// </summary>
public class OutboxHealthTests
{
    private const string Check = "events-outbox";

    private static readonly Dictionary<string, string?> Durable = new()
    {
        [SampleModuleExtensions.DurableEventsSetting] = "true"
    };

    // --- The readiness check. ---

    [Fact]
    public async Task TheCheck_IsHealthy_WithAnEmptyOutbox_AndIsPartOfReady()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = new HestiaApiFactory(finance.HostSettings(Durable));
        var client = factory.CreateClient();

        var result = await CheckAsync(factory);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task TheCheck_IsDegraded_WhenARowFailedTooOften_AndReadyStays200()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = new HestiaApiFactory(finance.HostSettings(Durable));
        var client = factory.CreateClient();
        await AddRowAsync(factory, attempts: 10, nextAttempt: DateTimeOffset.UtcNow.AddHours(1));

        var result = await CheckAsync(factory);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("sample: 1 row(s) failed 10 times", result.Description);
        Assert.Contains("retry-outbox sample", result.Description);
        // Degraded: the apps still work.
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Contains("Degraded", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheCheck_IsDegraded_WhenARowIsOlderThanWarnAfter()
    {
        // The sender is off, so the row waits.
        await using var factory = new HestiaApiFactory(new Dictionary<string, string?>(Durable)
        {
            ["Events:Outbox:Enabled"] = "false",
            ["Events:Outbox:WarnAfter"] = "00:10:00"
        });
        factory.CreateClient();
        await AddRowAsync(factory, attempts: 0, nextAttempt: DateTimeOffset.UtcNow, raised: DateTimeOffset.UtcNow.AddMinutes(-11));
        await AddRowAsync(factory, attempts: 0, nextAttempt: DateTimeOffset.UtcNow, raised: DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await CheckAsync(factory);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("sample: 1 row(s) older than 00:10:00", result.Description);
    }

    [Fact]
    public async Task TheCheck_IsHealthy_WithoutDurableModulesWithRows()
    {
        // The Sample module in its default (in-memory) mode; the Documents outbox is empty.
        await using var factory = new HestiaApiFactory();
        factory.CreateClient();

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(factory)).Status);
    }

    // --- retry-outbox. ---

    [Theory]
    [InlineData("sample")]
    [InlineData("all")]
    public async Task RetryOutbox_ResetsAStuckRow_AndTheSenderDeliversIt(string argument)
    {
        await using var factory = new HestiaApiFactory(Durable);
        factory.CreateClient();
        var stuck = new SampleItemCreated(Guid.NewGuid(), "Stuck kettle", DateTimeOffset.UtcNow);
        await AddRowAsync(factory, attempts: 10, nextAttempt: DateTimeOffset.UtcNow.AddHours(1), @event: stuck);
        // The sender does not send a row at MaxAttempts.
        Assert.Empty(await factory.Events.AfterQuietPeriodAsync<SampleItemCreated>());
        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(factory)).Status);

        var (exitCode, output) = await RunAsync(factory, argument);

        Assert.Equal(0, exitCode);
        // The in-memory test database has no schemas: every module context sees the same outbox rows. So "all" counts
        // the row once, but maybe under another schema.
        Assert.Contains(argument == "all" && !TestDatabase.OnPostgres ? "1 row(s) reset" : "sample: 1 row(s) reset", output);
        Assert.Contains("Reset 1 row(s)", output);
        var received = await factory.Events.WaitForAsync<SampleItemCreated>(e => e.ItemId == stuck.ItemId);
        Assert.Equal(stuck.EventId, received.EventId);
        await Eventually(() => OutboxAsync(factory), rows => rows.Count == 0);
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(factory)).Status);
    }

    [Fact]
    public async Task RetryOutbox_LeavesTheRowsThatStillWait()
    {
        await using var factory = new HestiaApiFactory(new Dictionary<string, string?>(Durable) { ["Events:Outbox:Enabled"] = "false" });
        factory.CreateClient();
        var later = DateTimeOffset.UtcNow.AddHours(1);
        await AddRowAsync(factory, attempts: 3, nextAttempt: later);
        await AddRowAsync(factory, attempts: 10, nextAttempt: later);

        var (exitCode, output) = await RunAsync(factory, "sample");

        Assert.Equal(0, exitCode);
        Assert.Contains("sample: 1 row(s) reset", output);
        var rows = await OutboxAsync(factory);
        Assert.Equal([3, 0], rows.Select(row => row.Attempts).ToList());
        Assert.Equal(later, rows[0].NextAttemptAt, TimeSpan.FromMilliseconds(1));
        Assert.True(rows[1].NextAttemptAt <= DateTimeOffset.UtcNow);
        // The last error stays, for diagnostics.
        Assert.All(rows, row => Assert.Equal("It failed.", row.LastError));
    }

    [Fact]
    public async Task RetryOutbox_SaysSo_WhenNoRowFailed()
    {
        await using var factory = new HestiaApiFactory(Durable);
        factory.CreateClient();

        var (exitCode, output) = await RunAsync(factory, "all");

        Assert.Equal(0, exitCode);
        Assert.Contains("No failed rows.", output);
    }

    [Theory]
    [InlineData]
    [InlineData("identity")]   // a module without durable events
    [InlineData("sample", "documents")]
    public async Task RetryOutbox_WithWrongArguments_Fails_AndNamesTheSchemas(params string[] arguments)
    {
        await using var factory = new HestiaApiFactory(Durable);
        factory.CreateClient();

        var (exitCode, output) = await RunAsync(factory, arguments);

        Assert.Equal(1, exitCode);
        Assert.Contains("documents, sample", output);
    }

    private static async Task<HealthReportEntry> CheckAsync(HestiaApiFactory factory)
    {
        var report = await factory.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Name == Check);
        return report.Entries[Check];
    }

    private static Task<int> AddRowAsync(HestiaApiFactory factory, int attempts, DateTimeOffset nextAttempt,
        DateTimeOffset? raised = null, SampleItemCreated? @event = null) =>
        factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<SampleDbContext>();
            @event ??= new SampleItemCreated(Guid.NewGuid(), "Kettle", DateTimeOffset.UtcNow);
            db.Set<OutboxMessage>().Add(new OutboxMessage
            {
                EventType = typeof(SampleItemCreated).FullName!,
                Payload = JsonSerializer.Serialize(@event),
                RaisedAt = raised ?? DateTimeOffset.UtcNow,
                Attempts = attempts,
                NextAttemptAt = nextAttempt,
                LastError = attempts > 0 ? "It failed." : null
            });
            return await db.SaveChangesAsync();
        });

    private static Task<List<OutboxMessage>> OutboxAsync(HestiaApiFactory factory) =>
        factory.WithScopeAsync(services => services.GetRequiredService<SampleDbContext>().Set<OutboxMessage>()
            .AsNoTracking().OrderBy(row => row.Id).ToListAsync());

    private static async Task<(int ExitCode, string Output)> RunAsync(HestiaApiFactory factory, params string[] arguments)
    {
        var command = factory.Services.GetServices<IModuleCommand>().Single(c => c.Name == "retry-outbox");
        var output = new StringWriter();
        var exitCode = await command.RunAsync(factory.Services, arguments, TextReader.Null, output, CancellationToken.None);
        return (exitCode, output.ToString());
    }

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var value = await read();
            if (done(value) || DateTimeOffset.UtcNow > deadline)
            {
                return value;
            }
            await Task.Delay(50);
        }
    }
}
