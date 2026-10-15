using Hestia.Contracts;

namespace Hestia.Modules.People.Contracts;

// The events of the People module (D7). The module uses durable events (T38): each event is stored in the outbox of the
// "people" schema with the change, and the host delivers it after the commit, also after a restart.
// An event can come more than once: make each consumer idempotent with the EventId (T36, the module event inbox).
// To read more than an event carries, use IPeopleQueries.

/// <summary>A person was added.</summary>
public sealed record PersonAdded(Guid PersonId, string Name, IReadOnlyList<string> Groups, DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>What changed in a person.</summary>
public enum PersonChange
{
    /// <summary>The user changed the details: the name, the groups, the birthday, the reminder, and so on.</summary>
    Details,

    /// <summary>A link was added or removed (by the user, or because the linked thing was deleted).</summary>
    Links
}

/// <summary>A person changed. The values are the ones after the change.</summary>
public sealed record PersonChanged(Guid PersonId, PersonChange Change, string Name, IReadOnlyList<string> Groups, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>A person was deleted, with the contacts, the gift ideas, the loans, and the links.</summary>
public sealed record PersonDeleted(Guid PersonId, string Name, DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>What happened to a contact, a gift idea, or a loan.</summary>
public enum PeopleRecordChange
{
    Added,
    Changed,
    Deleted
}

/// <summary>A contact with a person was logged, changed, or deleted.</summary>
/// <param name="InteractionId">The contact.</param>
/// <param name="PersonId">The person.</param>
/// <param name="PersonName">The name of the person.</param>
/// <param name="Change">Added, changed, or deleted.</param>
/// <param name="OccurredOn">The day of the contact.</param>
/// <param name="Kind">Free text, for example "Call", or null.</param>
/// <param name="OccurredAt">When it happened.</param>
public sealed record InteractionChanged(
    Guid InteractionId,
    Guid PersonId,
    string PersonName,
    PeopleRecordChange Change,
    DateOnly OccurredOn,
    string? Kind,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>A gift idea was added, changed (also: given), or deleted.</summary>
public sealed record GiftChanged(
    Guid GiftId,
    Guid PersonId,
    string PersonName,
    PeopleRecordChange Change,
    string Title,
    decimal? Price,
    DateOnly? GivenOn,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>A loan was added, changed (also: returned), or deleted.</summary>
public sealed record LoanChanged(
    Guid LoanId,
    Guid PersonId,
    string PersonName,
    PeopleRecordChange Change,
    string ItemName,
    LoanDirection Direction,
    DateOnly LentOn,
    DateOnly? DueOn,
    DateOnly? ReturnedOn,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}
