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
/// </remarks>
public interface IIntegrationEvent
{
    /// <summary>The contract version of the event type, from 1. The publisher sets it.</summary>
    int Version { get; }

    /// <summary>When the change happened, in UTC.</summary>
    DateTimeOffset OccurredAt { get; }
}
