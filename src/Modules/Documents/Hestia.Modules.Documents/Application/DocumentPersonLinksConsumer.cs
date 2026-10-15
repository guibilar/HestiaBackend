using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Documents.Infrastructure;
using Hestia.Modules.People.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.Documents.Application;

/// <summary>
/// Removes the links of documents to a deleted person (T93). The documents stay. Each removed link raises
/// <see cref="DocumentUnlinked"/>, as a link that the user removes. With the inbox (T36): the same event twice changes the
/// data once.
/// </summary>
public sealed class DocumentPersonLinksConsumer(DocumentsDbContext db, TimeProvider time, ILogger<DocumentPersonLinksConsumer> logger)
{
    /// <summary>The inbox name of this consumer: "&lt;Module&gt;.&lt;Class&gt;". Do not change it later.</summary>
    public const string InboxName = "Documents.DocumentPersonLinksConsumer";

    public async Task Consume(PersonDeleted message, CancellationToken cancellationToken)
    {
        if (!await db.TryReceiveAsync(message.EventId, InboxName, cancellationToken))
        {
            return; // Handled already.
        }
        var target = message.PersonId.ToString("D");
        var links = await db.Links
            .Where(l => l.Kind == DocumentLinkKinds.PeoplePerson && l.TargetId == target)
            .ToListAsync(cancellationToken);
        if (links.Count > 0)
        {
            var now = time.GetUtcNow();
            db.Links.RemoveRange(links);
            foreach (var link in links)
            {
                db.RaiseEvent(new DocumentUnlinked(link.DocumentId, link.Id, link.Kind, link.TargetId, now));
            }
            logger.LogInformation("Documents: removed {Links} link(s) to the deleted person {PersonId}", links.Count, message.PersonId);
        }
        // The inbox row commits also without a change.
        await db.SaveChangesAsync(cancellationToken);
    }
}
