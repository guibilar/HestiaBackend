using Hestia.Platform.Events;
using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Hestia.Platform.Tests;

/// <summary>The event inbox of a module (T36): <see cref="ModuleDbContext.TryReceiveAsync"/>.</summary>
public class EventInboxTests
{
    private const string Consumer = "Test.ThingAddedConsumer";

    private readonly string _database = Guid.NewGuid().ToString("N");

    [Fact]
    public async Task AnEvent_IsNewOnce_AfterTheSave()
    {
        var eventId = Guid.CreateVersion7();
        await using (var db = Inbox())
        {
            Assert.True(await db.TryReceiveAsync(eventId, Consumer));
            await db.SaveChangesAsync();
        }

        await using var later = Inbox();
        Assert.False(await later.TryReceiveAsync(eventId, Consumer));
        var entry = Assert.Single(later.Set<InboxEntry>());
        Assert.Equal(eventId, entry.EventId);
        Assert.Equal(Consumer, entry.Consumer);
    }

    [Fact]
    public async Task AnEvent_IsNotNewTwice_InOneUnsavedContext()
    {
        await using var db = Inbox();
        var eventId = Guid.CreateVersion7();

        Assert.True(await db.TryReceiveAsync(eventId, Consumer));
        Assert.False(await db.TryReceiveAsync(eventId, Consumer));
    }

    [Fact]
    public async Task WithoutASave_TheEventIsNotMarked()
    {
        var eventId = Guid.CreateVersion7();
        await using (var db = Inbox())
        {
            Assert.True(await db.TryReceiveAsync(eventId, Consumer));
        }

        await using var later = Inbox();
        Assert.True(await later.TryReceiveAsync(eventId, Consumer));
    }

    [Fact]
    public async Task EachConsumer_HandlesTheEventOnce()
    {
        await using var db = Inbox();
        var eventId = Guid.CreateVersion7();
        Assert.True(await db.TryReceiveAsync(eventId, Consumer));
        await db.SaveChangesAsync();

        Assert.True(await db.TryReceiveAsync(eventId, "Test.OtherConsumer"));
    }

    [Fact]
    public async Task AnEventWithoutAnId_IsRefused()
    {
        await using var db = Inbox();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => db.TryReceiveAsync(Guid.Empty, Consumer));

        Assert.Contains("EventId", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AConsumerWithoutAName_IsRefused(string consumer)
    {
        await using var db = Inbox();

        await Assert.ThrowsAsync<ArgumentException>(() => db.TryReceiveAsync(Guid.CreateVersion7(), consumer));
    }

    [Fact]
    public async Task AConsumerNameThatIsTooLong_IsRefused()
    {
        await using var db = Inbox();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            db.TryReceiveAsync(Guid.CreateVersion7(), new string('x', InboxEntry.MaxConsumerLength + 1)));
    }

    [Fact]
    public async Task AModuleWithoutTheInbox_GetsAClearError()
    {
        await using var db = new NoInboxDbContext(Options<NoInboxDbContext>());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.TryReceiveAsync(Guid.CreateVersion7(), Consumer));

        Assert.Contains("AddEventInbox", error.Message);
    }

    [Fact]
    public void TheInbox_IsInTheSchemaOfTheModule()
    {
        using var db = Inbox();

        // The design-time model has the relational names (as in the architecture tests).
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(InboxEntry))!;

        Assert.Equal(EventInboxModelBuilderExtensions.TableName, entity.GetTableName());
        Assert.Equal("test", entity.GetSchema());
        Assert.Equal([nameof(InboxEntry.EventId), nameof(InboxEntry.Consumer)],
            entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
    }

    private InboxDbContext Inbox() => new(Options<InboxDbContext>());

    private DbContextOptions<T> Options<T>() where T : DbContext =>
        new DbContextOptionsBuilder<T>().UseInMemoryDatabase(_database).Options;

    private sealed class InboxDbContext(DbContextOptions<InboxDbContext> options) : ModuleDbContext(options, "test")
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.AddEventInbox();
        }
    }

    private sealed class NoInboxDbContext(DbContextOptions<NoInboxDbContext> options) : ModuleDbContext(options, "test");
}
