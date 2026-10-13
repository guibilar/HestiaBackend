namespace Hestia.Platform.Events;

/// <summary>
/// Sends an event to its consumers in the host (in process). The host provides the implementation (Wolverine).
/// Modules do not call it directly: they call <see cref="Persistence.ModuleDbContext.RaiseEvent"/>, and
/// <see cref="ModuleEventsInterceptor"/> publishes the event after the database transaction commits (D7).
/// </summary>
public interface IEventPublisher
{
    /// <summary>Queues the event for its consumers, and returns at once (the in-memory path).</summary>
    ValueTask PublishAsync(object @event, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs all consumers of the event now, and returns when they finished (the outbox sender, T38). Throws when a
    /// consumer fails, so the sender keeps the event for a later try.
    /// </summary>
    Task DeliverAsync(object @event, CancellationToken cancellationToken = default);
}

/// <summary>The default until the host registers a real publisher. It fails, so a missing setup is clear.</summary>
internal sealed class MissingEventPublisher : IEventPublisher
{
    public ValueTask PublishAsync(object @event, CancellationToken cancellationToken = default) => throw Missing(@event);

    public Task DeliverAsync(object @event, CancellationToken cancellationToken = default) => throw Missing(@event);

    private static InvalidOperationException Missing(object @event) => new(
        $"No {nameof(IEventPublisher)} is registered, so the event {@event.GetType().Name} cannot be published. " +
        "The host registers one (Hestia.Api: AddHestiaEvents).");
}
