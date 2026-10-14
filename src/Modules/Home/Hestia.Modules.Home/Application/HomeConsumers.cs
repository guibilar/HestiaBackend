using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.Home.Domain;
using Hestia.Modules.Home.Infrastructure;
using Hestia.Modules.Tasks.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.Home.Application;

/// <summary>
/// Checks the warranty alerts after a change (T22): an item event checks that item; a document link to or from an item
/// checks that item; new details of a document (perhaps a new expiry date) check all items, because the "no double
/// alert" rule compares the dates. The Alerts module stores the event in its inbox with the result (T36), so an event
/// that comes again changes nothing.
/// </summary>
public sealed class HomeAlertsConsumer(IAlertChecks alerts)
{
    /// <summary>The inbox name of this consumer: "&lt;Module&gt;.&lt;Class&gt;". Do not change it later.</summary>
    public const string InboxName = "Home.HomeAlertsConsumer";

    public Task Consume(ItemAdded message, CancellationToken cancellationToken) =>
        CheckAsync(message.ItemId.ToString(), message.EventId, cancellationToken);

    public Task Consume(ItemChanged message, CancellationToken cancellationToken) =>
        CheckAsync(message.ItemId.ToString(), message.EventId, cancellationToken);

    public Task Consume(ItemDeleted message, CancellationToken cancellationToken) =>
        CheckAsync(message.ItemId.ToString(), message.EventId, cancellationToken);

    public Task Consume(DocumentChanged message, CancellationToken cancellationToken) =>
        message.Change == DocumentChange.Details ? CheckAsync(null, message.EventId, cancellationToken) : Task.CompletedTask;

    public Task Consume(DocumentLinked message, CancellationToken cancellationToken) =>
        message.LinkKind == DocumentLinkKinds.HomeItem ? CheckAsync(message.TargetId, message.EventId, cancellationToken) : Task.CompletedTask;

    public Task Consume(DocumentUnlinked message, CancellationToken cancellationToken) =>
        message.LinkKind == DocumentLinkKinds.HomeItem ? CheckAsync(message.TargetId, message.EventId, cancellationToken) : Task.CompletedTask;

    private Task CheckAsync(string? itemId, Guid eventId, CancellationToken cancellationToken) =>
        alerts.CheckAsync(WarrantyAlerts.SourceName, itemId, new AlertTrigger(eventId, InboxName), cancellationToken);
}

/// <summary>
/// Removes the links of items to a deleted document or a deleted task (T22). The items stay. Each item that loses a link
/// raises <see cref="ItemChanged"/> (<see cref="ItemChange.Links"/>). With the inbox (T36): the same event twice changes
/// the data once.
/// </summary>
public sealed class ItemLinkCleanupConsumer(HomeDbContext db, TimeProvider time, ILogger<ItemLinkCleanupConsumer> logger)
{
    /// <summary>The inbox name of this consumer: "&lt;Module&gt;.&lt;Class&gt;". Do not change it later.</summary>
    public const string InboxName = "Home.ItemLinkCleanupConsumer";

    public async Task Consume(DocumentDeleted message, CancellationToken cancellationToken)
    {
        if (!await db.TryReceiveAsync(message.EventId, InboxName, cancellationToken))
        {
            return; // Handled already.
        }
        await RemoveLinksAsync(ItemLinkKinds.DocumentsDocument, message.DocumentId, cancellationToken);
    }

    public async Task Consume(TaskDeleted message, CancellationToken cancellationToken)
    {
        if (!await db.TryReceiveAsync(message.EventId, InboxName, cancellationToken))
        {
            return; // Handled already.
        }
        await RemoveLinksAsync(ItemLinkKinds.TasksTask, message.TaskId, cancellationToken);
    }

    private async Task RemoveLinksAsync(string kind, Guid targetId, CancellationToken cancellationToken)
    {
        var target = targetId.ToString("D");
        var links = await db.Links.Where(l => l.Kind == kind && l.TargetId == target).ToListAsync(cancellationToken);
        if (links.Count > 0)
        {
            var itemIds = links.Select(l => l.ItemId).Distinct().ToList();
            var items = await db.Items.Where(i => itemIds.Contains(i.Id)).ToListAsync(cancellationToken);
            var now = time.GetUtcNow();
            db.Links.RemoveRange(links);
            foreach (var item in items)
            {
                item.UpdatedAt = now;
                db.RaiseEvent(new ItemChanged(item.Id, ItemChange.Links, item.Name, item.RoomId, item.Category, item.Quantity,
                    item.Value, item.WarrantyUntil, ItemTags.Read(item.Tags), now));
            }
            logger.LogInformation("Home: removed {Links} link(s) to the deleted {Kind} {TargetId}", links.Count, kind, targetId);
        }
        // The inbox row commits also without a change.
        await db.SaveChangesAsync(cancellationToken);
    }
}
