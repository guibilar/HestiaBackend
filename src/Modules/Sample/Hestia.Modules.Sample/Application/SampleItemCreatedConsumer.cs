using Hestia.Modules.Sample.Contracts;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.Sample.Application;

/// <summary>
/// The template for an event consumer. Wolverine finds it in the module assembly: a public class whose name ends in
/// "Consumer", with a public <c>Consume</c> method that takes the event first. Constructor and method parameters come
/// from the service container. The same consumer gets an event from a module (in process) or from finance (RabbitMQ).
/// </summary>
/// <remarks>
/// An event can come more than once (finance events after a retry), so make a consumer idempotent.
/// When a consumer throws, Wolverine retries it; the change that raised the event stays committed.
/// </remarks>
public sealed class SampleItemCreatedConsumer(SampleActivity activity, ILogger<SampleItemCreatedConsumer> logger)
{
    public void Consume(SampleItemCreated message)
    {
        activity.Record(message);
        logger.LogInformation("Sample item {ItemId} \"{Name}\" was created", message.ItemId, message.Name);
    }
}

/// <summary>What the Sample consumer saw since the host started: a small, visible result for the template.</summary>
public sealed class SampleActivity
{
    private int _itemsCreated;
    private string? _lastItemName;

    public void Record(SampleItemCreated message)
    {
        Interlocked.Increment(ref _itemsCreated);
        Volatile.Write(ref _lastItemName, message.Name);
    }

    public SampleActivityDto Read() => new(Volatile.Read(ref _itemsCreated), Volatile.Read(ref _lastItemName));
}
