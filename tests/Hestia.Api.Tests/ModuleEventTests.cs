using System.Collections.Concurrent;
using System.Net.Http.Json;
using Hestia.Api.Events;
using Hestia.Contracts.Finance;
using Hestia.Modules.Sample.Application;
using Hestia.Modules.Sample.Contracts;
using Hestia.Modules.Sample.Domain;
using Hestia.Modules.Sample.Infrastructure;
using Hestia.Platform.Modules;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hestia.Api.Tests;

/// <summary>
/// In-process module events (D7): a module raises an event on its DbContext, and its consumers get it only after the
/// change is committed. The Sample module is the template (SampleItemCreated, SampleItemCreatedConsumer).
/// </summary>
public class ModuleEventTests
{
    private const string Items = "/api/sample/items";

    [Fact]
    public async Task ModuleEvent_ReachesItsConsumers_AfterTheSave()
    {
        await using var factory = new HestiaApiFactory();
        var client = factory.CreateClient();

        var created = await (await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle"))).ReadAsync<SampleItemDto>();

        var received = await factory.Events.WaitForAsync<SampleItemCreated>();
        Assert.Equal(created.Id, received.ItemId);
        Assert.Equal("Kettle", received.Name);
        Assert.Equal(created.CreatedAt, received.OccurredAt);
        Assert.Equal(1, received.Version);
    }

    [Fact]
    public async Task SampleConsumer_SeesEachCreatedItem()
    {
        await using var factory = new HestiaApiFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle"));
        await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("Teapot"));

        var activity = await Eventually(async () => await client.GetFromJsonAsync<SampleActivityDto>("/api/sample/activity"),
            dto => dto?.ItemsCreated == 2);

        Assert.Equal(2, activity!.ItemsCreated);
    }

    [Fact]
    public async Task ModuleEvent_WithoutASave_IsNotPublished()
    {
        await using var factory = new HestiaApiFactory();
        factory.CreateClient();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            db.RaiseEvent(new SampleItemCreated(Guid.NewGuid(), "Never saved", DateTimeOffset.UtcNow));
            Assert.Equal(1, db.UnpublishedEventCount);
        }

        Assert.Empty(await factory.Events.AfterQuietPeriodAsync<SampleItemCreated>());
    }

    [Fact]
    public async Task ModuleEvent_InATransaction_IsPublishedOnlyAfterTheCommit()
    {
        Assert.SkipUnless(TestDatabase.OnPostgres, $"Needs PostgreSQL: set {TestDatabase.Variable}.");
        await using var factory = new HestiaApiFactory();
        factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<SampleItemService>();

        SampleItemDto? created = null;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            created = await service.CreateAsync(new CreateSampleItemRequest("Kettle"), CancellationToken.None);

            // Saved, not committed: no consumer has it yet.
            Assert.Empty(await factory.Events.AfterQuietPeriodAsync<SampleItemCreated>());
            Assert.Equal(1, db.UnpublishedEventCount);

            await transaction.CommitAsync();
        });

        var received = await factory.Events.WaitForAsync<SampleItemCreated>();
        Assert.Equal(created!.Id, received.ItemId);
        Assert.Equal(0, db.UnpublishedEventCount);
    }

    [Fact]
    public async Task ModuleEvent_InATransactionThatRollsBack_IsNeverPublished()
    {
        Assert.SkipUnless(TestDatabase.OnPostgres, $"Needs PostgreSQL: set {TestDatabase.Variable}.");
        await using var factory = new HestiaApiFactory();
        factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<SampleItemService>();

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await service.CreateAsync(new CreateSampleItemRequest("Kettle"), CancellationToken.None);
            await transaction.RollbackAsync();
        });

        Assert.Empty(await factory.Events.AfterQuietPeriodAsync<SampleItemCreated>());
        Assert.Equal(0, db.UnpublishedEventCount);
    }

    [Fact]
    public async Task ModuleEvent_OfAFailedSave_IsNotPublished_AndStaysForTheNextSave()
    {
        Assert.SkipUnless(TestDatabase.OnPostgres, $"Needs PostgreSQL: set {TestDatabase.Variable}.");
        await using var factory = new HestiaApiFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle"));
        await factory.Events.WaitForAsync<SampleItemCreated>();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();

        // The same name as a stored item: the unique index refuses the save.
        var duplicate = new SampleItem { Id = Guid.CreateVersion7(), Name = "Kettle", CreatedAt = DateTimeOffset.UtcNow };
        db.Items.Add(duplicate);
        db.RaiseEvent(new SampleItemCreated(duplicate.Id, duplicate.Name, duplicate.CreatedAt));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.DoesNotContain(await factory.Events.AfterQuietPeriodAsync<SampleItemCreated>(), e => e.ItemId == duplicate.Id);
        Assert.Equal(1, db.UnpublishedEventCount);

        // Corrected, the next save includes the event.
        duplicate.Name = "Kettle 2";
        await db.SaveChangesAsync();
        await factory.Events.WaitForAsync<SampleItemCreated>(e => e.ItemId == duplicate.Id);
    }

    [Fact]
    public void EventTypes_AreTheFinanceEvents_AndTheModuleEvents()
    {
        var types = HestiaEvents.EventTypes(ModuleLoader.Discover());

        Assert.Contains(typeof(SampleItemCreated), types);
        Assert.All(FinanceEventTopology.EventTypes, type => Assert.Contains(type, types));
    }

    [Fact]
    public async Task DebugConsumer_LogsEachEvent_WhenLogAllIsOn()
    {
        var logs = new LogCapture();
        await using var factory = new HestiaApiFactory(
            new Dictionary<string, string?> { ["Events:LogAll"] = "true" },
            services => services.AddSingleton<ILoggerProvider>(logs));
        var client = factory.CreateClient();

        var created = await (await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle"))).ReadAsync<SampleItemDto>();

        var line = await Eventually(() => Task.FromResult(logs.Find("Event received: SampleItemCreated")), found => found is not null);
        Assert.Contains($"ItemId = {created.Id}", line);
        Assert.Contains("Name = Kettle", line);
    }

    [Fact]
    public async Task DebugConsumer_IsQuiet_WhenLogAllIsOff()
    {
        var logs = new LogCapture();
        // The test host runs in Development, where appsettings.Development.json turns LogAll on.
        await using var factory = new HestiaApiFactory(
            new Dictionary<string, string?> { ["Events:LogAll"] = "false" },
            services => services.AddSingleton<ILoggerProvider>(logs));
        var client = factory.CreateClient();

        await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle"));
        await Eventually(async () => await client.GetFromJsonAsync<SampleActivityDto>("/api/sample/activity"), dto => dto?.ItemsCreated == 1);

        Assert.Null(logs.Find("Event received:"));
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

    /// <summary>Keeps every log line of the host.</summary>
    private sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public string? Find(string text) => _lines.FirstOrDefault(line => line.Contains(text, StringComparison.Ordinal));

        public ILogger CreateLogger(string categoryName) => new Logger(_lines);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => lines.Enqueue(formatter(state, exception));
        }
    }
}
