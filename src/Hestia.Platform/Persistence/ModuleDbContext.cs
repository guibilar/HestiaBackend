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

    // How many raised events the save in progress includes.
    private int _inSave;

    public string Schema { get; } = schema;

    /// <summary>
    /// Queues a module event. The event is published to its consumers in the host after the next
    /// <c>SaveChanges</c> of this context commits: at once without an explicit transaction, else when the
    /// transaction commits. A failed save or a rolled-back transaction publishes nothing (D7).
    /// </summary>
    /// <remarks>Put the event type in the module's <c>.Contracts</c> project, so other modules can consume it.</remarks>
    public void RaiseEvent(object @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        _raised.Add(@event);
    }

    /// <summary>The events that this context holds and did not publish yet (raised or waiting for a commit).</summary>
    public int UnpublishedEventCount => _raised.Count + _waitingForCommit.Count;

    internal void SaveStarting() => _inSave = _raised.Count;

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

    /// <summary>The transaction rolled back: the events of its saves did not happen.</summary>
    internal void DropUncommitted() => _waitingForCommit.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        base.OnModelCreating(modelBuilder);
    }
}
