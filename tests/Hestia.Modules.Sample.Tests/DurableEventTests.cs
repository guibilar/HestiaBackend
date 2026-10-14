using System.Net.Http.Json;
using System.Text.Json;
using Hestia.Modules.Sample.Application;
using Hestia.Modules.Sample.Contracts;
using Hestia.Modules.Sample.Domain;
using Hestia.Modules.Sample.Infrastructure;
using Hestia.Platform.Events;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;

namespace Hestia.Modules.Sample.Tests;

/// <summary>
/// Durable module events (T38). With <c>Sample:DurableEvents</c>, the Sample module stores each event in its outbox
/// (the table <c>sample.EventOutbox</c>) in the same transaction as the change, and the host's outbox sender delivers
/// it. Without the setting, the Sample module uses the in-memory path (the default): see <c>ModuleEventTests</c>.
/// </summary>
public class DurableEventTests
{
    private const string Items = "/api/sample/items";

    private static readonly Dictionary<string, string?> Durable = new()
    {
        [SampleModuleExtensions.DurableEventsSetting] = "true"
    };

    /// <summary>Durable events, and a stopped sender: the events stay in the outbox.</summary>
    private static readonly Dictionary<string, string?> DurableWithoutSender = new(Durable)
    {
        ["Events:Outbox:Enabled"] = "false"
    };

    [Fact]
    public async Task DurableEvent_ReachesItsConsumers_AndLeavesTheOutbox()
    {
        await using var factory = new HestiaApiFactory(Durable);
        var client = factory.CreateClient();

        var created = await (await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle"))).ReadAsync<SampleItemDto>();

        var received = await factory.Events.WaitForAsync<SampleItemCreated>();
        Assert.Equal(created.Id, received.ItemId);
        Assert.Equal("Kettle", received.Name);
        Assert.Equal(created.CreatedAt, received.OccurredAt);
        Assert.Equal(2, received.Version);
        await Eventually(() => OutboxAsync(factory), rows => rows.Count == 0);
        var activity = await Eventually(async () => await client.GetFromJsonAsync<SampleActivityDto>("/api/sample/activity"),
            dto => dto?.ItemsCreated == 1);
        Assert.Equal(new SampleActivityDto(1, "Kettle"), activity);
    }

    [Theory]
    [InlineData(2020)]
    [InlineData(2040)]
    public async Task DurableEvent_WithAFixedClock_ReachesItsConsumers(int year)
    {
        // T70: the save and the sender use the same clock, also when it is far from the real time.
        var now = new DateTimeOffset(year, 6, 15, 12, 0, 0, TimeSpan.Zero);
        await using var factory = new HestiaApiFactory(Durable, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedClock(now));
        });
        var client = factory.CreateClient();

        var created = await (await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle"))).ReadAsync<SampleItemDto>();

        var received = await factory.Events.WaitForAsync<SampleItemCreated>();
        Assert.Equal((created.Id, now), (received.ItemId, received.OccurredAt));
        await Eventually(() => OutboxAsync(factory), rows => rows.Count == 0);
        Assert.Empty(await OutboxAsync(factory));
        var activity = await Eventually(async () => await client.GetFromJsonAsync<SampleActivityDto>("/api/sample/activity"),
            dto => dto?.ItemsCreated == 1);
        Assert.Equal(1, activity!.ItemsCreated);
        var handledAt = await factory.WithScopeAsync(services => services.GetRequiredService<SampleDbContext>()
            .Set<InboxEntry>().AsNoTracking().Select(entry => entry.HandledAt).SingleAsync());
        Assert.Equal(now, handledAt);
    }

    [Fact]
    public async Task DurableEvent_WithAStoppedSender_HasTheTimesOfTheFixedClock()
    {
        var now = new DateTimeOffset(2020, 6, 15, 12, 0, 0, TimeSpan.Zero);
        await using var factory = new HestiaApiFactory(DurableWithoutSender, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedClock(now));
        });

        await factory.CreateClient().PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle"));

        var row = Assert.Single(await OutboxAsync(factory));
        Assert.Equal((now, now), (row.RaisedAt, row.NextAttemptAt));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task DurableEvent_IsStoredInTheOutbox_WithTheChange()
    {
        await using var factory = new HestiaApiFactory(DurableWithoutSender);
        var client = factory.CreateClient();

        var created = await (await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle"))).ReadAsync<SampleItemDto>();

        var row = Assert.Single(await OutboxAsync(factory));
        Assert.Equal(typeof(SampleItemCreated).FullName, row.EventType);
        Assert.Equal(created.Id, StoredEvent(row).ItemId);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.LastError);
        // The sender is stopped: no consumer has the event.
        Assert.Empty(await factory.Events.AfterQuietPeriodAsync<SampleItemCreated>());
    }

    [Fact]
    public async Task DurableEvent_SurvivesAHostRestart_BetweenTheCommitAndTheSend()
    {
        // The first host commits the change and its event, and stops before it sends the event.
        var first = new HestiaApiFactory(DurableWithoutSender);
        var created = await (await first.CreateClient().PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle")))
            .ReadAsync<SampleItemDto>();
        var stored = StoredEvent(Assert.Single(await OutboxAsync(first)));
        Assert.Empty(await first.Events.AfterQuietPeriodAsync<SampleItemCreated>());
        await using var second = first.Restarted(Durable);
        await first.DisposeAsync();

        // The next host, on the same database, sends it.
        var client = second.CreateClient();

        var received = await second.Events.WaitForAsync<SampleItemCreated>(e => e.ItemId == created.Id);
        Assert.Equal(stored.EventId, received.EventId);
        await Eventually(() => OutboxAsync(second), rows => rows.Count == 0);
        var activity = await Eventually(async () => await client.GetFromJsonAsync<SampleActivityDto>("/api/sample/activity"),
            dto => dto?.ItemsCreated == 1);
        Assert.Equal(new SampleActivityDto(1, "Kettle"), activity);
    }

    [Fact]
    public async Task DurableEvent_DeliveredAgainAfterARestart_ChangesTheDataOnce()
    {
        // A stop after the delivery and before the row is deleted: the next host delivers the event again.
        await using var factory = new HestiaApiFactory(Durable);
        var client = factory.CreateClient();
        await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle"));
        var first = await factory.Events.WaitForAsync<SampleItemCreated>();
        await Eventually(() => OutboxAsync(factory), rows => rows.Count == 0);

        // The same event in the outbox again, as if the delete had not happened.
        await factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<SampleDbContext>();
            db.Set<OutboxMessage>().Add(new OutboxMessage
            {
                EventType = typeof(SampleItemCreated).FullName!,
                Payload = JsonSerializer.Serialize(first),
                RaisedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            });
            return await db.SaveChangesAsync();
        });
        factory.Services.GetRequiredService<ModuleOutboxSignal>().Wake();

        await Eventually(() => Task.FromResult(factory.Events.Events.OfType<SampleItemCreated>().Count()), count => count == 2);
        await Eventually(() => OutboxAsync(factory), rows => rows.Count == 0);
        var activity = await client.GetFromJsonAsync<SampleActivityDto>("/api/sample/activity");
        Assert.Equal(1, activity!.ItemsCreated);
    }

    [Fact]
    public async Task DurableEvent_OfATransactionThatRollsBack_IsNeverStored()
    {
        Assert.SkipUnless(TestDatabase.OnPostgres, $"Needs PostgreSQL: set {TestDatabase.Variable}.");
        await using var factory = new HestiaApiFactory(Durable);
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

        Assert.Empty(await OutboxAsync(factory));
        Assert.Empty(await factory.Events.AfterQuietPeriodAsync<SampleItemCreated>());
    }

    [Fact]
    public async Task DurableEvent_InATransaction_IsSentAfterTheCommit()
    {
        Assert.SkipUnless(TestDatabase.OnPostgres, $"Needs PostgreSQL: set {TestDatabase.Variable}.");
        await using var factory = new HestiaApiFactory(Durable);
        factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<SampleItemService>();

        SampleItemDto? created = null;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            created = await service.CreateAsync(new CreateSampleItemRequest("Kettle"), CancellationToken.None);

            // Saved, not committed: the sender cannot see the row yet.
            Assert.Empty(await factory.Events.AfterQuietPeriodAsync<SampleItemCreated>());

            await transaction.CommitAsync();
        });

        var received = await factory.Events.WaitForAsync<SampleItemCreated>();
        Assert.Equal(created!.Id, received.ItemId);
    }

    [Fact]
    public async Task DurableEvent_OfAFailedSave_IsNotStored_AndGoesWithTheNextSave()
    {
        Assert.SkipUnless(TestDatabase.OnPostgres, $"Needs PostgreSQL: set {TestDatabase.Variable}.");
        await using var factory = new HestiaApiFactory(Durable);
        var client = factory.CreateClient();
        await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle"));
        await factory.Events.WaitForAsync<SampleItemCreated>();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();

        // The same name as a stored item: the unique index refuses the save, with its outbox row.
        var duplicate = new SampleItem { Id = Guid.CreateVersion7(), Name = "Kettle", CreatedAt = DateTimeOffset.UtcNow };
        db.Items.Add(duplicate);
        db.RaiseEvent(new SampleItemCreated(duplicate.Id, duplicate.Name, duplicate.CreatedAt));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.DoesNotContain(await factory.Events.AfterQuietPeriodAsync<SampleItemCreated>(), e => e.ItemId == duplicate.Id);

        // Corrected, the next save stores the event, and the sender sends it.
        duplicate.Name = "Kettle 2";
        await db.SaveChangesAsync();
        await factory.Events.WaitForAsync<SampleItemCreated>(e => e.ItemId == duplicate.Id);
    }

    [Fact]
    public async Task DurableEvent_WithAFailingConsumer_StaysInTheOutbox_ForALaterTry()
    {
        await using var factory = new HestiaApiFactory(
            new Dictionary<string, string?>(Durable) { ["Events:Outbox:RetryDelay"] = "01:00:00" },
            services => services.AddSingleton<IWolverineExtension>(new AddFailingConsumer()));
        var client = factory.CreateClient();

        await client.PostAsJsonAsync(Items, new CreateSampleItemRequest(FailingSampleConsumer.FailFor));

        var rows = await Eventually(() => OutboxAsync(factory), found => found.Count == 1 && found[0].Attempts == 1);
        var row = Assert.Single(rows);
        Assert.Contains(FailingSampleConsumer.Error, row.LastError);
        Assert.True(row.NextAttemptAt > DateTimeOffset.UtcNow.AddMinutes(30), $"The next try is at {row.NextAttemptAt}.");

        // Other events do not wait for it.
        await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("Teapot"));
        await factory.Events.WaitForAsync<SampleItemCreated>(e => e.Name == "Teapot");
        await Eventually(() => OutboxAsync(factory), found => found.Count == 1 && found[0].EventType == row.EventType);
    }

    [Fact]
    public async Task InMemoryMode_StoresNoOutboxRow()
    {
        // The default: the Sample module does not use durable events.
        await using var factory = new HestiaApiFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync(Items, new CreateSampleItemRequest("Kettle"));

        await factory.Events.WaitForAsync<SampleItemCreated>();
        Assert.Empty(await OutboxAsync(factory));
    }

    private static Task<List<OutboxMessage>> OutboxAsync(HestiaApiFactory factory) =>
        factory.WithScopeAsync(services => services.GetRequiredService<SampleDbContext>().Set<OutboxMessage>()
            .AsNoTracking().OrderBy(row => row.Id).ToListAsync());

    private static SampleItemCreated StoredEvent(OutboxMessage row) =>
        JsonSerializer.Deserialize<SampleItemCreated>(row.Payload)!;

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

/// <summary>A consumer that fails for one item name, to test a failed delivery from the outbox.</summary>
public sealed class FailingSampleConsumer
{
    public const string FailFor = "Broken kettle";
    public const string Error = "This consumer fails on purpose.";

    public void Consume(SampleItemCreated message)
    {
        if (message.Name == FailFor)
        {
            throw new InvalidOperationException(Error);
        }
    }
}

/// <summary>Adds <see cref="FailingSampleConsumer"/> to one test host.</summary>
public sealed class AddFailingConsumer : IWolverineExtension
{
    public void Configure(WolverineOptions options) => options.Discovery.IncludeType(typeof(FailingSampleConsumer));
}
