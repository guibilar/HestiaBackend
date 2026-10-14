using System.Text.Json;
using Hestia.Platform.Events;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Platform.Tests;

/// <summary>
/// The durable save path of module events (T38): with <see cref="ModuleDatabaseOptions.UseDurableEvents"/>, a save
/// writes each raised event to the module's outbox, in the same save as the change, and publishes nothing in memory.
/// The host's sender (Hestia.Api) delivers the rows; its tests are in the Sample module tests.
/// </summary>
public class EventOutboxTests
{
    private readonly string _database = Guid.NewGuid().ToString("N");
    private readonly RecordingPublisher _publisher = new();

    [Fact]
    public async Task DurableModule_StoresTheEventWithTheChange_AndPublishesNothing()
    {
        await using var services = Services<OutboxDbContext>(durable: true);
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
            var thing = new Thing { Id = Guid.CreateVersion7(), Name = "Kettle" };
            db.Things.Add(thing);
            db.RaiseEvent(new ThingAdded(thing.Id, thing.Name));
            await db.SaveChangesAsync();
            Assert.Equal(0, db.UnpublishedEventCount);
        }

        await using var read = services.CreateAsyncScope();
        var stored = read.ServiceProvider.GetRequiredService<OutboxDbContext>();
        Assert.Single(stored.Things);
        var row = Assert.Single(stored.Set<OutboxMessage>());
        Assert.Equal(nameof(ThingAdded), row.EventType);
        Assert.Equal("Kettle", JsonSerializer.Deserialize<ThingAdded>(row.Payload)!.Name);
        Assert.Equal(0, row.Attempts);
        Assert.Equal(row.RaisedAt, row.NextAttemptAt);
        Assert.Empty(_publisher.Published);
    }

    [Fact]
    public async Task DurableModule_KeepsTheOrderOfItsEvents()
    {
        await using var services = Services<OutboxDbContext>(durable: true);
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
            foreach (var name in new[] { "Cup", "Apron", "Broom" })
            {
                db.RaiseEvent(new ThingAdded(Guid.CreateVersion7(), name));
            }
            await db.SaveChangesAsync();
        }

        await using var read = services.CreateAsyncScope();
        var names = read.ServiceProvider.GetRequiredService<OutboxDbContext>().Set<OutboxMessage>()
            .OrderBy(row => row.Id).AsEnumerable()
            .Select(row => JsonSerializer.Deserialize<ThingAdded>(row.Payload)!.Name);
        Assert.Equal(["Cup", "Apron", "Broom"], names);
    }

    [Fact]
    public async Task DurableModule_WakesTheSender_AfterTheCommit()
    {
        await using var services = Services<OutboxDbContext>(durable: true);
        var signal = services.GetRequiredService<ModuleOutboxSignal>();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();

        // A save without events does not wake it.
        db.Things.Add(new Thing { Id = Guid.CreateVersion7(), Name = "Kettle" });
        await db.SaveChangesAsync();
        Assert.False(await signal.WaitAsync(TimeSpan.Zero));

        db.RaiseEvent(new ThingAdded(Guid.CreateVersion7(), "Teapot"));
        await db.SaveChangesAsync();
        Assert.True(await signal.WaitAsync(TimeSpan.Zero));
    }

    [Theory]
    [InlineData(2020)]
    [InlineData(2040)]
    public async Task DurableModule_WritesTheTimesOfARow_WithTheHostClock(int year)
    {
        // T70: the outbox sender compares NextAttemptAt with the host clock, so the save must use the same clock.
        var now = new DateTimeOffset(year, 3, 4, 5, 6, 7, TimeSpan.Zero);
        await using var services = Services<OutboxDbContext>(durable: true, new FixedClock(now));
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
            db.RaiseEvent(new ThingAdded(Guid.CreateVersion7(), "Kettle"));
            await db.SaveChangesAsync();
        }

        await using var read = services.CreateAsyncScope();
        var row = Assert.Single(read.ServiceProvider.GetRequiredService<OutboxDbContext>().Set<OutboxMessage>());
        Assert.Equal((now, now), (row.RaisedAt, row.NextAttemptAt));
    }

    [Fact]
    public async Task DurableModule_WithoutAHostClock_UsesTheRealClock()
    {
        await using var services = Services<OutboxDbContext>(durable: true);
        var before = DateTimeOffset.UtcNow;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
            db.RaiseEvent(new ThingAdded(Guid.CreateVersion7(), "Kettle"));
            await db.SaveChangesAsync();
        }

        await using var read = services.CreateAsyncScope();
        var row = Assert.Single(read.ServiceProvider.GetRequiredService<OutboxDbContext>().Set<OutboxMessage>());
        Assert.InRange(row.RaisedAt, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task InMemoryModule_PublishesAfterTheSave_AndStoresNoRow()
    {
        await using var services = Services<OutboxDbContext>(durable: false);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();

        db.RaiseEvent(new ThingAdded(Guid.CreateVersion7(), "Kettle"));
        await db.SaveChangesAsync();

        Assert.IsType<ThingAdded>(Assert.Single(_publisher.Published));
        Assert.Empty(db.Set<OutboxMessage>());
    }

    [Fact]
    public async Task DurableModule_WithAnEventThatCannotBeStored_FailsTheSave()
    {
        await using var services = Services<OutboxDbContext>(durable: true);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
        db.Things.Add(new Thing { Id = Guid.CreateVersion7(), Name = "Kettle" });
        db.RaiseEvent("not a module event");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        Assert.Contains("module event", error.Message);
        await using var read = services.CreateAsyncScope();
        Assert.Empty(read.ServiceProvider.GetRequiredService<OutboxDbContext>().Things);
    }

    [Fact]
    public async Task DurableModule_WithoutTheOutbox_GetsAClearError()
    {
        await using var services = Services<NoOutboxDbContext>(durable: true);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NoOutboxDbContext>();
        db.RaiseEvent(new ThingAdded(Guid.CreateVersion7(), "Kettle"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        Assert.Contains("AddEventOutbox", error.Message);
    }

    [Fact]
    public void TheOutbox_IsInTheSchemaOfTheModule()
    {
        using var db = new OutboxDbContext(new DbContextOptionsBuilder<OutboxDbContext>().UseInMemoryDatabase(_database).Options);

        // The design-time model has the relational names (as in the architecture tests).
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(OutboxMessage))!;

        Assert.Equal(EventOutboxModelBuilderExtensions.TableName, entity.GetTableName());
        Assert.Equal("test", entity.GetSchema());
        Assert.Equal([nameof(OutboxMessage.Id)], entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
    }

    [Fact]
    public void TheDefault_IsTheInMemoryPath()
    {
        var services = new ServiceCollection();
        services.AddModuleDbContext<OutboxDbContext>("test");
        services.AddModuleDbContext<NoOutboxDbContext>("other", database => database.UseDurableEvents());

        using var provider = services.BuildServiceProvider();
        var databases = provider.GetServices<ModuleDatabase>().ToList();

        Assert.False(databases.Single(d => d.ContextType == typeof(OutboxDbContext)).DurableEvents);
        Assert.True(databases.Single(d => d.ContextType == typeof(NoOutboxDbContext)).DurableEvents);
    }

    [Fact]
    public async Task TheSignal_IsOneWake_ForManyCommits()
    {
        var signal = new ModuleOutboxSignal();

        signal.Wake();
        signal.Wake();

        Assert.True(await signal.WaitAsync(TimeSpan.Zero));
        Assert.False(await signal.WaitAsync(TimeSpan.Zero));
    }

    private ServiceProvider Services<TContext>(bool durable, TimeProvider? clock = null) where TContext : ModuleDbContext
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (clock is not null)
        {
            services.AddSingleton(clock);
        }
        // Before AddModuleDbContext: the platform keeps these (TryAdd).
        services.AddSingleton<IDatabaseOptionsConfigurator>(new InMemoryDatabase(_database));
        services.AddSingleton<IEventPublisher>(_publisher);
        services.AddSingleton<IModuleEventSerializer, ThingSerializer>();
        services.AddModuleDbContext<TContext>("test", database => database.UseDurableEvents(durable));
        return services.BuildServiceProvider();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public sealed record ThingAdded(Guid ThingId, string Name);

    public sealed class Thing
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class OutboxDbContext(DbContextOptions<OutboxDbContext> options) : ModuleDbContext(options, "test")
    {
        public DbSet<Thing> Things => Set<Thing>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Thing>().ToTable("Things");
            modelBuilder.AddEventOutbox();
        }
    }

    public sealed class NoOutboxDbContext(DbContextOptions<NoOutboxDbContext> options) : ModuleDbContext(options, "other");

    private sealed class InMemoryDatabase(string name) : IDatabaseOptionsConfigurator
    {
        public void Configure(DbContextOptionsBuilder options, string schema) => options.UseInMemoryDatabase(name);
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<object> Published { get; } = [];

        public ValueTask PublishAsync(object @event, CancellationToken cancellationToken = default)
        {
            Published.Add(@event);
            return ValueTask.CompletedTask;
        }

        public Task DeliverAsync(object @event, CancellationToken cancellationToken = default)
        {
            Published.Add(@event);
            return Task.CompletedTask;
        }
    }

    /// <summary>Knows one event type, as the host's serializer knows the module events.</summary>
    private sealed class ThingSerializer : IModuleEventSerializer
    {
        public string TypeName(object @event) => @event is ThingAdded
            ? nameof(ThingAdded)
            : throw new InvalidOperationException($"{@event.GetType().Name} is not a module event.");

        public string Serialize(object @event) => JsonSerializer.Serialize(@event, @event.GetType());

        public object Deserialize(string typeName, string payload) => JsonSerializer.Deserialize<ThingAdded>(payload)!;
    }
}
