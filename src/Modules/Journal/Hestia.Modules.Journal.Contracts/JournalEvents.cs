using Hestia.Contracts;

namespace Hestia.Modules.Journal.Contracts;

// The events of the Journal module (D7). The module uses durable events (T38, T78): each event is stored in the outbox
// of the "journal" schema with the change, and the host delivers it after the commit, also after a restart.
// An event can come more than once: make each consumer idempotent with the EventId (T36, the module event inbox).
// To read more than an event carries, use IJournalQueries.

/// <summary>The entry of a day was written: a new entry, or a changed text.</summary>
/// <param name="Created">True for a new entry; false for a change.</param>
public sealed record JournalEntrySaved(Guid EntryId, DateOnly Day, bool Created, DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>The entry of a day was deleted.</summary>
public sealed record JournalEntryDeleted(Guid EntryId, DateOnly Day, DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}
