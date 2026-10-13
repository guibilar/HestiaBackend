namespace Hestia.Contracts;

/// <summary>
/// An event that crosses a module or a process boundary: a finance event that comes through RabbitMQ, or a module event
/// in the host. A consumer gets the event in the same way on both paths.
/// </summary>
/// <remarks>
/// The rules for a contract change:
/// <list type="bullet">
/// <item>Add a property with a default value: the old consumers ignore it. Increase <see cref="Version"/>.</item>
/// <item>Remove or rename a property, or change its meaning: make a new event type. Keep the old one until no publisher sends it.</item>
/// </list>
/// Each event type implements the two properties with these defaults:
/// <code>
/// public int Version { get; init; } = 1;
/// public Guid EventId { get; init; } = Guid.CreateVersion7();
/// </code>
/// </remarks>
public interface IIntegrationEvent
{
    /// <summary>The contract version of the event type, from 1. The publisher sets it.</summary>
    int Version { get; }

    /// <summary>
    /// The identity of this event: a new value for each event that the publisher makes, and the same value each time the
    /// event is delivered again (a retry, or a second delivery from the broker). A consumer uses it to handle each event
    /// once (T36). The default (a new GUID for each new event object) is correct for a publisher: do not reuse a value
    /// for a different event. Version 2 of each Hestia event added it.
    /// </summary>
    /// <remarks>
    /// An event from a publisher that uses an older contract version has no value on the wire. The consumer then gets
    /// a new value for each delivery, so it cannot see the second delivery of that event.
    /// </remarks>
    Guid EventId { get; }

    /// <summary>When the change happened, in UTC.</summary>
    DateTimeOffset OccurredAt { get; }
}
