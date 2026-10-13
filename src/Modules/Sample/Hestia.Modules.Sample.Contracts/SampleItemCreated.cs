using Hestia.Contracts;

namespace Hestia.Modules.Sample.Contracts;

/// <summary>
/// A public module event: a sample item was created. The Sample module raises it on its DbContext
/// (<c>RaiseEvent</c>), and the host publishes it in process after the transaction commits.
/// Any module can consume it (see <c>SampleItemCreatedConsumer</c>, the template for a consumer).
/// </summary>
public sealed record SampleItemCreated(Guid ItemId, string Name, DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 2;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}
