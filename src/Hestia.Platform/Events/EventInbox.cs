using Hestia.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Platform.Events;

/// <summary>
/// A row of a module's event inbox (T36): this consumer handled this event. The key is the pair, so two consumers in
/// the same module each handle the event once.
/// </summary>
public sealed class InboxEntry
{
    public const int MaxConsumerLength = 200;

    /// <summary>The <c>EventId</c> of the event.</summary>
    public Guid EventId { get; set; }

    /// <summary>The stable name of the consumer, for example <c>Sample.SampleItemCreatedConsumer</c>.</summary>
    public string Consumer { get; set; } = string.Empty;

    /// <summary>When the consumer handled the event, in UTC. For diagnostics only.</summary>
    public DateTimeOffset HandledAt { get; set; }
}

/// <summary>Adds the event inbox to a module's model.</summary>
public static class EventInboxModelBuilderExtensions
{
    /// <summary>The table name, in the module's schema.</summary>
    public const string TableName = "EventInbox";

    /// <summary>
    /// Adds the table <c>EventInbox</c> to the module's schema. Call it in <c>OnModelCreating</c> of a module that has
    /// a consumer that changes data, then add a migration. See <see cref="ModuleDbContext.TryReceiveAsync"/>.
    /// </summary>
    public static ModelBuilder AddEventInbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InboxEntry>(entry =>
        {
            entry.ToTable(TableName);
            entry.HasKey(e => new { e.EventId, e.Consumer });
            entry.Property(e => e.Consumer).HasMaxLength(InboxEntry.MaxConsumerLength);
        });
        return modelBuilder;
    }
}
