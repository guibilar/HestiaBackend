using Hestia.Modules.Sample.Contracts;
using Hestia.Modules.Sample.Domain;
using Hestia.Modules.Sample.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.Sample.Application;

/// <summary>
/// The template for an event consumer. Wolverine finds it in the module assembly: a public class whose name ends in
/// "Consumer", with a public <c>Consume</c> method that takes the event first. Constructor and method parameters come
/// from the service container. The same consumer gets an event from a module (in process) or from finance (RabbitMQ).
/// </summary>
/// <remarks>
/// An event can come more than once (RabbitMQ delivers again after a failure or a lost acknowledgement), so the consumer
/// is idempotent (T36): it asks the module's event inbox first (<c>TryReceiveAsync</c>), and saves its change and the
/// inbox row in one save. When a consumer throws, its change does not commit; the change that raised the event stays
/// committed.
/// </remarks>
public sealed class SampleItemCreatedConsumer(SampleDbContext db, TimeProvider time, ILogger<SampleItemCreatedConsumer> logger)
{
    /// <summary>The inbox name of this consumer: "&lt;Module&gt;.&lt;Class&gt;". Do not change it later.</summary>
    public const string InboxName = "Sample.SampleItemCreatedConsumer";

    public async Task Consume(SampleItemCreated message, CancellationToken cancellationToken)
    {
        // 1. Handled already? Then do nothing.
        if (!await db.TryReceiveAsync(message.EventId, InboxName, cancellationToken))
        {
            logger.LogDebug("Event {EventId} came again: the Sample consumer handled it already", message.EventId);
            return;
        }

        // 2. The change, and 3. one save: the change and the inbox row commit together.
        db.Activity.Add(new SampleActivityEntry
        {
            Id = Guid.CreateVersion7(),
            ItemId = message.ItemId,
            Name = message.Name,
            ReceivedAt = time.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Sample item {ItemId} \"{Name}\" was created", message.ItemId, message.Name);
    }
}

/// <summary>What the Sample consumer handled: a small, visible result for the template.</summary>
public sealed class SampleActivityQueries(SampleDbContext db)
{
    public async Task<SampleActivityDto> ReadAsync(CancellationToken cancellationToken)
    {
        var count = await db.Activity.CountAsync(cancellationToken);
        var last = await db.Activity.AsNoTracking()
            .OrderByDescending(entry => entry.ReceivedAt)
            .ThenByDescending(entry => entry.Id)
            .Select(entry => entry.Name)
            .FirstOrDefaultAsync(cancellationToken);
        return new SampleActivityDto(count, last);
    }
}
