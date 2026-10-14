using Hestia.Contracts;

namespace Hestia.Modules.Home.Contracts;

// The events of the Home module (D7). The module uses durable events (T38): each event is stored in the outbox of the
// "home" schema with the change, and the host delivers it after the commit, also after a restart.
// An event can come more than once: make each consumer idempotent with the EventId (T36, the module event inbox).
// To read more than an event carries, use IHomeQueries.

/// <summary>An item was added.</summary>
/// <param name="ItemId">The item.</param>
/// <param name="Name">The name.</param>
/// <param name="RoomId">The room, or null.</param>
/// <param name="Category">The category, or null.</param>
/// <param name="Quantity">How many.</param>
/// <param name="Value">The value of the row (see <see cref="ItemSummaryDto.Value"/>).</param>
/// <param name="WarrantyUntil">The last day of the warranty, or null.</param>
/// <param name="Tags">The tags.</param>
/// <param name="OccurredAt">When the item was added.</param>
public sealed record ItemAdded(
    Guid ItemId,
    string Name,
    Guid? RoomId,
    string? Category,
    int Quantity,
    decimal Value,
    DateOnly? WarrantyUntil,
    IReadOnlyList<string> Tags,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>What changed in an item.</summary>
public enum ItemChange
{
    /// <summary>The user changed the details: the name, the room, the prices, the warranty, the tags, and so on.</summary>
    Details,

    /// <summary>A link was added or removed (by the user, or because the linked document or task was deleted).</summary>
    Links
}

/// <summary>An item changed. The values are the ones after the change.</summary>
public sealed record ItemChanged(
    Guid ItemId,
    ItemChange Change,
    string Name,
    Guid? RoomId,
    string? Category,
    int Quantity,
    decimal Value,
    DateOnly? WarrantyUntil,
    IReadOnlyList<string> Tags,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>An item was deleted, with its links. Other modules remove their links to it (kind <c>Home.Item</c>).</summary>
public sealed record ItemDeleted(Guid ItemId, string Name, DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>What happened to a room.</summary>
public enum RoomChange
{
    Added,
    Renamed,
    Deleted
}

/// <summary>A room was added, renamed, or deleted. A new order of the rooms raises no event.</summary>
/// <param name="RoomId">The room.</param>
/// <param name="Change">Added, renamed, or deleted.</param>
/// <param name="Name">The name (after a rename: the new name).</param>
/// <param name="OldName">After a rename: the old name. Else null.</param>
/// <param name="OccurredAt">When it happened.</param>
public sealed record RoomChanged(Guid RoomId, RoomChange Change, string Name, string? OldName, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}
