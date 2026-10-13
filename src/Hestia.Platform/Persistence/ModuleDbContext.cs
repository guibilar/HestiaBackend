using Hestia.Platform.Events;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Platform.Persistence;

/// <summary>
/// The base class for a module's DbContext. All its tables are in the module's own schema
/// (the lower-case module name). No module reads another module's tables.
/// </summary>
public abstract class ModuleDbContext(DbContextOptions options, string schema) : DbContext(options)
{
    // Raised, but not saved yet.
    private readonly List<object> _raised = [];

    // Saved inside an explicit transaction: published when the transaction commits.
    private readonly List<object> _waitingForCommit = [];

    // How many raised events the save in progress includes (in-memory path).
    private int _inSave;

    // Durable events (T38): a save wrote outbox rows, so the sender gets a signal after the commit.
    private bool _outboxWritten;

    public string Schema { get; } = schema;

    /// <summary>
    /// Queues a module event. The event is published to its consumers in the host after the next
    /// <c>SaveChanges</c> of this context commits: at once without an explicit transaction, else when the
    /// transaction commits. A failed save or a rolled-back transaction publishes nothing (D7).
    /// </summary>
    /// <remarks>
    /// <para>Put the event type in the module's <c>.Contracts</c> project, so other modules can consume it.</para>
    /// <para>A module with durable events (T38, <see cref="ModuleDatabaseOptions.UseDurableEvents"/>): the save writes the
    /// event to the module's outbox, in the same transaction as the change. The host's outbox sender delivers it after
    /// the commit, also after a restart.</para>
    /// </remarks>
    public void RaiseEvent(object @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        _raised.Add(@event);
    }

    /// <summary>
    /// The idempotency check of a consumer (T36). It returns false when <paramref name="consumer"/> handled the event
    /// already: then do nothing. Else it adds an inbox row to this context and returns true: then make the change, and
    /// save it with one <c>SaveChangesAsync</c>. The inbox row and the change commit together, or neither does.
    /// </summary>
    /// <param name="eventId">The <c>EventId</c> of the event.</param>
    /// <param name="consumer">A stable name of the consumer, for example <c>Sample.SampleItemCreatedConsumer</c>. Do not
    /// change it later: a consumer with a new name handles old events again.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <remarks>
    /// <list type="bullet">
    /// <item>The module's model needs the inbox: call <c>modelBuilder.AddEventInbox()</c> in <c>OnModelCreating</c>.</item>
    /// <item>Two deliveries of the same event at the same time: one save fails on the inbox key, and its change does
    /// not commit. Wolverine handles that failure as any consumer failure (a retry gets false). The other delivery's
    /// change stays, so the data changes once.</item>
    /// <item>A consumer that must save more than once: put the saves in one transaction, with the execution strategy
    /// (<c>Database.CreateExecutionStrategy()</c>), and call this method inside it.</item>
    /// </list>
    /// </remarks>
    public async Task<bool> TryReceiveAsync(Guid eventId, string consumer, CancellationToken cancellationToken = default)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("The event has no EventId. The publisher must set it.", nameof(eventId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        if (consumer.Length > InboxEntry.MaxConsumerLength)
        {
            throw new ArgumentException($"The consumer name can have at most {InboxEntry.MaxConsumerLength} characters.", nameof(consumer));
        }
        if (Model.FindEntityType(typeof(InboxEntry)) is null)
        {
            throw new InvalidOperationException(
                $"{GetType().Name} has no event inbox. Call modelBuilder.{nameof(EventInboxModelBuilderExtensions.AddEventInbox)}() " +
                "in OnModelCreating, and add a migration.");
        }

        var inbox = Set<InboxEntry>();
        if (inbox.Local.Any(e => e.EventId == eventId && e.Consumer == consumer) ||
            await inbox.AnyAsync(e => e.EventId == eventId && e.Consumer == consumer, cancellationToken))
        {
            return false;
        }
        inbox.Add(new InboxEntry { EventId = eventId, Consumer = consumer, HandledAt = DateTimeOffset.UtcNow });
        return true;
    }

    /// <summary>
    /// The events that this context holds in memory and did not publish yet (raised, or waiting for a commit). In a
    /// module with durable events, a save moves the raised events to outbox rows, so they do not count here.
    /// </summary>
    public int UnpublishedEventCount => _raised.Count + _waitingForCommit.Count;

    /// <summary>A save starts. With <paramref name="outbox"/> (durable events), the raised events become outbox rows.</summary>
    internal void SaveStarting(IModuleEventSerializer? outbox)
    {
        if (outbox is null)
        {
            _inSave = _raised.Count;
            return;
        }
        _inSave = 0;
        WriteOutbox(outbox);
    }

    /// <summary>
    /// Adds an outbox row for each raised event, so the save stores the events with the change (T38). A failed save
    /// keeps the rows as added, so a later save that succeeds stores them.
    /// </summary>
    private void WriteOutbox(IModuleEventSerializer serializer)
    {
        if (_raised.Count == 0)
        {
            return;
        }
        if (Model.FindEntityType(typeof(OutboxMessage)) is null)
        {
            throw new InvalidOperationException(
                $"{GetType().Name} uses durable events, but has no event outbox. Call " +
                $"modelBuilder.{nameof(EventOutboxModelBuilderExtensions.AddEventOutbox)}() in OnModelCreating, and add a migration.");
        }

        // All rows first: an event that cannot be stored fails the save before any row is added.
        var now = DateTimeOffset.UtcNow;
        var rows = _raised.Select(@event => new OutboxMessage
        {
            EventType = serializer.TypeName(@event),
            Payload = serializer.Serialize(@event),
            RaisedAt = now,
            NextAttemptAt = now
        }).ToList();
        Set<OutboxMessage>().AddRange(rows);
        _raised.Clear();
        _outboxWritten = true;
    }

    /// <summary>The save succeeded: the events that it included are now stored with the change.</summary>
    internal void SaveSucceeded()
    {
        _waitingForCommit.AddRange(_raised.Take(_inSave));
        _raised.RemoveRange(0, _inSave);
        _inSave = 0;
    }

    /// <summary>The save failed: its events stay raised, for a later save that succeeds.</summary>
    internal void SaveFailed() => _inSave = 0;

    /// <summary>Takes the events that the committed change includes.</summary>
    internal List<object> TakeCommitted()
    {
        var events = _waitingForCommit.ToList();
        _waitingForCommit.Clear();
        return events;
    }

    /// <summary>True once after a commit that stored outbox rows (durable events): then wake the sender.</summary>
    internal bool TakeOutboxWritten()
    {
        var written = _outboxWritten;
        _outboxWritten = false;
        return written;
    }

    /// <summary>The transaction rolled back: the events of its saves did not happen.</summary>
    internal void DropUncommitted()
    {
        _waitingForCommit.Clear();
        _outboxWritten = false;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        base.OnModelCreating(modelBuilder);
    }
}
