using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Documents.Infrastructure;
using Hestia.Modules.Home.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.Documents.Application;

/// <summary>
/// Removes the links of documents to a deleted home item (T22). The documents stay. Each removed link raises
/// <see cref="DocumentUnlinked"/>, as a link that the user removes. With the inbox (T36): the same event twice changes the
/// data once.
/// </summary>
public sealed class DocumentItemLinksConsumer(DocumentsDbContext db, TimeProvider time, ILogger<DocumentItemLinksConsumer> logger)
{
    /// <summary>The inbox name of this consumer: "&lt;Module&gt;.&lt;Class&gt;". Do not change it later.</summary>
    public const string InboxName = "Documents.DocumentItemLinksConsumer";

    public async Task Consume(ItemDeleted message, CancellationToken cancellationToken)
    {
        if (!await db.TryReceiveAsync(message.EventId, InboxName, cancellationToken))
        {
            return; // Handled already.
        }
        var target = message.ItemId.ToString("D");
        var links = await db.Links
            .Where(l => l.Kind == DocumentLinkKinds.HomeItem && l.TargetId == target)
            .ToListAsync(cancellationToken);
        if (links.Count > 0)
        {
            var now = time.GetUtcNow();
            db.Links.RemoveRange(links);
            foreach (var link in links)
            {
                db.RaiseEvent(new DocumentUnlinked(link.DocumentId, link.Id, link.Kind, link.TargetId, now));
            }
            logger.LogInformation("Documents: removed {Links} link(s) to the deleted home item {ItemId}", links.Count, message.ItemId);
        }
        // The inbox row commits also without a change.
        await db.SaveChangesAsync(cancellationToken);
    }
}
