using System.Net.Http.Json;
using Hestia.Modules.Sample.Application;
using Hestia.Modules.Sample.Contracts;
using Hestia.Modules.Sample.Infrastructure;
using Hestia.Platform.Events;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;

namespace Hestia.Modules.Sample.Tests;

/// <summary>
/// The Sample consumer is the template for an idempotent consumer (T36): the module's event inbox lets it handle each
/// event (each <c>EventId</c>) once, however often the event comes.
/// </summary>
public class IdempotentConsumerTests
{
    [Fact]
    public async Task TheSameEvent_DeliveredTwice_ChangesTheDataOnce()
    {
        await using var factory = new HestiaApiFactory();
        factory.CreateClient();
        var message = new SampleItemCreated(Guid.NewGuid(), "Kettle", DateTimeOffset.UtcNow);

        await DeliverAsync(factory, message);
        await DeliverAsync(factory, message);

        var activity = await factory.CreateClient().GetFromJsonAsync<SampleActivityDto>("/api/sample/activity");
        Assert.Equal(new SampleActivityDto(1, "Kettle"), activity);
        Assert.Equal(1, await InboxRowsAsync(factory, message.EventId));
        // Both deliveries reached the consumers: the inbox, not the transport, kept the second one out.
        Assert.Equal(2, factory.Events.Events.OfType<SampleItemCreated>().Count(e => e.EventId == message.EventId));
    }

    [Fact]
    public async Task TwoEvents_WithTheSameValues_AreTwoChanges()
    {
        await using var factory = new HestiaApiFactory();
        factory.CreateClient();
        var first = new SampleItemCreated(Guid.NewGuid(), "Kettle", DateTimeOffset.UtcNow);
        var second = first with { EventId = Guid.CreateVersion7() };

        await DeliverAsync(factory, first);
        await DeliverAsync(factory, second);

        var activity = await factory.CreateClient().GetFromJsonAsync<SampleActivityDto>("/api/sample/activity");
        Assert.Equal(2, activity!.ItemsCreated);
    }

    [Fact]
    public async Task TheSameEvent_DeliveredTwiceAtTheSameTime_ChangesTheDataOnce()
    {
        Assert.SkipUnless(TestDatabase.OnPostgres, $"Needs PostgreSQL: set {TestDatabase.Variable}.");
        await using var factory = new HestiaApiFactory();
        factory.CreateClient();
        var message = new SampleItemCreated(Guid.NewGuid(), "Kettle", DateTimeOffset.UtcNow);

        // Two consumers, each with its own scope and DbContext, as two parallel deliveries have.
        var results = await Task.WhenAll(ConsumeDirectlyAsync(factory, message), ConsumeDirectlyAsync(factory, message));
        // When both checks ran before either save, the second save fails on the inbox key and its change does not
        // commit. Wolverine then handles it again (a retry), and the inbox says "handled".
        Assert.All(results, error => Assert.True(error is null or DbUpdateException, error?.ToString()));
        Assert.Null(await ConsumeDirectlyAsync(factory, message));

        var activity = await factory.CreateClient().GetFromJsonAsync<SampleActivityDto>("/api/sample/activity");
        Assert.Equal(1, activity!.ItemsCreated);
        Assert.Equal(1, await InboxRowsAsync(factory, message.EventId));
    }

    /// <summary>A delivery through Wolverine: all consumers of the event run, and the call waits for them.</summary>
    private static async Task DeliverAsync(HestiaApiFactory factory, SampleItemCreated message)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message);
    }

    /// <summary>Runs the Sample consumer alone, in a new scope. Returns the exception, or null.</summary>
    private static async Task<Exception?> ConsumeDirectlyAsync(HestiaApiFactory factory, SampleItemCreated message)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var consumer = new SampleItemCreatedConsumer(scope.ServiceProvider.GetRequiredService<SampleDbContext>(),
            TimeProvider.System, NullLogger<SampleItemCreatedConsumer>.Instance);
        try
        {
            await consumer.Consume(message, CancellationToken.None);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Task<int> InboxRowsAsync(HestiaApiFactory factory, Guid eventId) =>
        factory.WithScopeAsync(services => services.GetRequiredService<SampleDbContext>().Set<InboxEntry>()
            .CountAsync(entry => entry.EventId == eventId && entry.Consumer == SampleItemCreatedConsumer.InboxName));
}
