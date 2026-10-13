using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Platform.Events;

/// <summary>
/// A row of a module's event outbox (T38): an event that a module with durable events raised. The row is stored in the
/// same transaction as the change that raised the event, so the event is never lost after the commit. The host's
/// outbox sender delivers it to the consumers, and deletes the row when all consumers finished.
/// </summary>
/// <remarks>Modules do not read or write these rows. The platform writes them, and the host's sender reads them.</remarks>
public sealed class OutboxMessage
{
    public const int MaxEventTypeLength = 300;
    public const int MaxErrorLength = 2000;

    /// <summary>The order in which the module raised the events (an identity column).</summary>
    public long Id { get; set; }

    /// <summary>The full name of the event type, for example <c>Hestia.Modules.Sample.Contracts.SampleItemCreated</c>.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>The event as JSON, with its <c>EventId</c>, so each try delivers the same event.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>When the change was saved, in UTC.</summary>
    public DateTimeOffset RaisedAt { get; set; }

    /// <summary>How many deliveries failed. At <c>Events:Outbox:MaxAttempts</c>, the sender stops and keeps the row.</summary>
    public int Attempts { get; set; }

    /// <summary>The earliest time for the next try, in UTC.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>The error of the last failed try, for diagnostics.</summary>
    public string? LastError { get; set; }
}

/// <summary>Adds the event outbox to a module's model.</summary>
public static class EventOutboxModelBuilderExtensions
{
    /// <summary>The table name, in the module's schema.</summary>
    public const string TableName = "EventOutbox";

    /// <summary>
    /// Adds the table <c>EventOutbox</c> to the module's schema. Call it in <c>OnModelCreating</c> of a module that uses
    /// durable events (<see cref="ModuleDatabaseOptions.UseDurableEvents"/>), then add a migration.
    /// </summary>
    public static ModelBuilder AddEventOutbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(message =>
        {
            message.ToTable(TableName);
            message.HasKey(m => m.Id);
            message.Property(m => m.EventType).HasMaxLength(OutboxMessage.MaxEventTypeLength);
            message.Property(m => m.LastError).HasMaxLength(OutboxMessage.MaxErrorLength);
            message.HasIndex(m => m.NextAttemptAt);
        });
        return modelBuilder;
    }
}

/// <summary>
/// Turns a module event into an outbox row and back (T38). The host provides the implementation: it knows the event
/// types of all modules.
/// </summary>
public interface IModuleEventSerializer
{
    /// <summary>
    /// The name to store for the event's type. Throws <see cref="InvalidOperationException"/> for an event that is not a
    /// module event (a type in a module's <c>.Contracts</c> project that implements <c>IIntegrationEvent</c>).
    /// </summary>
    string TypeName(object @event);

    /// <summary>The event as JSON.</summary>
    string Serialize(object @event);

    /// <summary>The event from a stored type name and its JSON. Throws for an unknown type name.</summary>
    object Deserialize(string typeName, string payload);
}

/// <summary>The default until the host registers a real serializer. It fails, so a missing setup is clear.</summary>
internal sealed class MissingModuleEventSerializer : IModuleEventSerializer
{
    public string TypeName(object @event) => throw Missing();

    public string Serialize(object @event) => throw Missing();

    public object Deserialize(string typeName, string payload) => throw Missing();

    private static InvalidOperationException Missing() => new(
        $"No {nameof(IModuleEventSerializer)} is registered, so durable module events cannot be stored. " +
        "The host registers one (Hestia.Api: AddHestiaEvents).");
}

/// <summary>
/// Wakes the host's outbox sender when a durable module commits new events, so the events go out at once and not at
/// the next poll. One signal is enough for any number of commits.
/// </summary>
public sealed class ModuleOutboxSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>Asks the sender to read the outboxes now.</summary>
    public void Wake()
    {
        if (_signal.CurrentCount > 0)
        {
            return;
        }
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another commit woke the sender at the same time.
        }
    }

    /// <summary>Waits for a <see cref="Wake"/> or the timeout. Returns true for a wake.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        _signal.WaitAsync(timeout, cancellationToken);
}
