using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.People.Contracts;
using Hestia.Modules.People.Domain;
using Hestia.Modules.People.Infrastructure;
using Hestia.Modules.Tasks.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.People.Application;

/// <summary>
/// Checks the People alerts after a change of a person, a contact, or a loan (T20). The check covers all people (see
/// <see cref="ReminderAlerts"/>). The Alerts module stores the event in its inbox with the result (T36).
/// </summary>
public sealed class PeopleAlertsConsumer(IAlertChecks alerts)
{
    /// <summary>The inbox name of this consumer: "&lt;Module&gt;.&lt;Class&gt;". Do not change it later.</summary>
    public const string InboxName = "People.PeopleAlertsConsumer";

    public Task Consume(PersonAdded message, CancellationToken cancellationToken) => CheckAsync(message.EventId, cancellationToken);

    public Task Consume(PersonChanged message, CancellationToken cancellationToken) =>
        message.Change == PersonChange.Details ? CheckAsync(message.EventId, cancellationToken) : Task.CompletedTask;

    public Task Consume(PersonDeleted message, CancellationToken cancellationToken) => CheckAsync(message.EventId, cancellationToken);

    public Task Consume(InteractionChanged message, CancellationToken cancellationToken) => CheckAsync(message.EventId, cancellationToken);

    public Task Consume(LoanChanged message, CancellationToken cancellationToken) => CheckAsync(message.EventId, cancellationToken);

    private Task CheckAsync(Guid eventId, CancellationToken cancellationToken) =>
        alerts.CheckAsync(ReminderAlerts.SourceName, null, new AlertTrigger(eventId, InboxName), cancellationToken);
}

/// <summary>
/// Removes the links of people to a deleted document, task, or home item (T20). The people stay. Each person who loses a
/// link raises <see cref="PersonChanged"/> (<see cref="PersonChange.Links"/>). With the inbox (T36).
/// </summary>
public sealed class PersonLinkCleanupConsumer(PeopleDbContext db, TimeProvider time, ILogger<PersonLinkCleanupConsumer> logger)
{
    /// <summary>The inbox name of this consumer: "&lt;Module&gt;.&lt;Class&gt;". Do not change it later.</summary>
    public const string InboxName = "People.PersonLinkCleanupConsumer";

    public async Task Consume(DocumentDeleted message, CancellationToken cancellationToken)
    {
        if (!await db.TryReceiveAsync(message.EventId, InboxName, cancellationToken))
        {
            return; // Handled already.
        }
        await RemoveLinksAsync(PersonLinkKinds.DocumentsDocument, message.DocumentId, cancellationToken);
    }

    public async Task Consume(TaskDeleted message, CancellationToken cancellationToken)
    {
        if (!await db.TryReceiveAsync(message.EventId, InboxName, cancellationToken))
        {
            return; // Handled already.
        }
        await RemoveLinksAsync(PersonLinkKinds.TasksTask, message.TaskId, cancellationToken);
    }

    public async Task Consume(ItemDeleted message, CancellationToken cancellationToken)
    {
        if (!await db.TryReceiveAsync(message.EventId, InboxName, cancellationToken))
        {
            return; // Handled already.
        }
        await RemoveLinksAsync(PersonLinkKinds.HomeItem, message.ItemId, cancellationToken);
    }

    private async Task RemoveLinksAsync(string kind, Guid targetId, CancellationToken cancellationToken)
    {
        var target = targetId.ToString("D");
        var links = await db.Links.Where(l => l.Kind == kind && l.TargetId == target).ToListAsync(cancellationToken);
        if (links.Count > 0)
        {
            var personIds = links.Select(l => l.PersonId).Distinct().ToList();
            var people = await db.People.Where(p => personIds.Contains(p.Id)).ToListAsync(cancellationToken);
            var now = time.GetUtcNow();
            db.Links.RemoveRange(links);
            foreach (var person in people)
            {
                person.UpdatedAt = now;
                db.RaiseEvent(new PersonChanged(person.Id, PersonChange.Links, person.Name, PersonGroups.Read(person.Groups), now));
            }
            logger.LogInformation("People: removed {Links} link(s) to the deleted {Kind} {TargetId}", links.Count, kind, targetId);
        }
        // The inbox row commits also without a change.
        await db.SaveChangesAsync(cancellationToken);
    }
}
