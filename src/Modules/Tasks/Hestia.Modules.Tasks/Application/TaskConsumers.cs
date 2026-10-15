using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.People.Contracts;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Modules.Tasks.Domain;
using Hestia.Modules.Tasks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.Tasks.Application;

/// <summary>
/// Checks the overdue alerts after each task event (T19): a new task, a new due date, a "done", an undo, or a delete.
/// The check covers all tasks (the count decides between one alert per task and one alert for many; see
/// <see cref="TaskDueAlerts"/>). The Alerts module stores the event in its inbox with the result (T36), so an event that
/// comes again changes nothing.
/// </summary>
public sealed class TaskAlertsConsumer(IAlertChecks alerts)
{
    /// <summary>The inbox name of this consumer: "&lt;Module&gt;.&lt;Class&gt;". Do not change it later.</summary>
    public const string InboxName = "Tasks.TaskAlertsConsumer";

    public Task Consume(TaskCreated message, CancellationToken cancellationToken) => CheckAsync(message.EventId, cancellationToken);

    public Task Consume(TaskChanged message, CancellationToken cancellationToken) =>
        message.Change == TaskChange.Details ? CheckAsync(message.EventId, cancellationToken) : Task.CompletedTask;

    public Task Consume(TaskCompleted message, CancellationToken cancellationToken) => CheckAsync(message.EventId, cancellationToken);

    public Task Consume(TaskReopened message, CancellationToken cancellationToken) => CheckAsync(message.EventId, cancellationToken);

    public Task Consume(TaskDeleted message, CancellationToken cancellationToken) => CheckAsync(message.EventId, cancellationToken);

    private Task CheckAsync(Guid eventId, CancellationToken cancellationToken) =>
        alerts.CheckAsync(TaskDueAlerts.SourceName, null, new AlertTrigger(eventId, InboxName), cancellationToken);
}

/// <summary>
/// Removes the links to a deleted document (T19). The tasks stay. Each task that loses a link raises
/// <see cref="TaskChanged"/> (<see cref="TaskChange.Links"/>). With the inbox (T36): the same event twice changes the data
/// once.
/// </summary>
public sealed class DocumentDeletedConsumer(TasksDbContext db, TimeProvider time, ILogger<DocumentDeletedConsumer> logger)
{
    /// <summary>The inbox name of this consumer: "&lt;Module&gt;.&lt;Class&gt;". Do not change it later.</summary>
    public const string InboxName = "Tasks.DocumentDeletedConsumer";

    public async Task Consume(DocumentDeleted message, CancellationToken cancellationToken)
    {
        if (!await db.TryReceiveAsync(message.EventId, InboxName, cancellationToken))
        {
            return; // Handled already.
        }
        var target = message.DocumentId.ToString("D");
        var links = await db.Links
            .Where(l => l.Kind == TaskLinkKinds.DocumentsDocument && l.TargetId == target)
            .ToListAsync(cancellationToken);
        if (links.Count > 0)
        {
            var taskIds = links.Select(l => l.TaskId).Distinct().ToList();
            var tasks = await db.Tasks.Where(t => taskIds.Contains(t.Id)).ToListAsync(cancellationToken);
            var now = time.GetUtcNow();
            db.Links.RemoveRange(links);
            foreach (var task in tasks)
            {
                task.UpdatedAt = now;
                db.RaiseEvent(new TaskChanged(task.Id, TaskChange.Links, task.Title, task.DueOn, TaskTags.Read(task.Tags),
                    task.Repeat, task.Status, now));
            }
            logger.LogInformation("Tasks: removed {Links} link(s) to the deleted document {DocumentId}", links.Count, message.DocumentId);
        }
        // The inbox row commits also without a change.
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Removes the links of tasks to a deleted home item (T22). The tasks stay. Each task that loses a link raises
/// <see cref="TaskChanged"/> (<see cref="TaskChange.Links"/>). With the inbox (T36): the same event twice changes the data
/// once.
/// </summary>
public sealed class TaskItemLinksConsumer(TasksDbContext db, TimeProvider time, ILogger<TaskItemLinksConsumer> logger)
{
    /// <summary>The inbox name of this consumer: "&lt;Module&gt;.&lt;Class&gt;". Do not change it later.</summary>
    public const string InboxName = "Tasks.TaskItemLinksConsumer";

    public async Task Consume(ItemDeleted message, CancellationToken cancellationToken)
    {
        if (!await db.TryReceiveAsync(message.EventId, InboxName, cancellationToken))
        {
            return; // Handled already.
        }
        var target = message.ItemId.ToString("D");
        var links = await db.Links
            .Where(l => l.Kind == TaskLinkKinds.HomeItem && l.TargetId == target)
            .ToListAsync(cancellationToken);
        if (links.Count > 0)
        {
            var taskIds = links.Select(l => l.TaskId).Distinct().ToList();
            var tasks = await db.Tasks.Where(t => taskIds.Contains(t.Id)).ToListAsync(cancellationToken);
            var now = time.GetUtcNow();
            db.Links.RemoveRange(links);
            foreach (var task in tasks)
            {
                task.UpdatedAt = now;
                db.RaiseEvent(new TaskChanged(task.Id, TaskChange.Links, task.Title, task.DueOn, TaskTags.Read(task.Tags),
                    task.Repeat, task.Status, now));
            }
            logger.LogInformation("Tasks: removed {Links} link(s) to the deleted home item {ItemId}", links.Count, message.ItemId);
        }
        // The inbox row commits also without a change.
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Removes the links of tasks to a deleted person (T93). The tasks stay. Each task that loses a link raises
/// <see cref="TaskChanged"/> (<see cref="TaskChange.Links"/>). With the inbox (T36).
/// </summary>
public sealed class TaskPersonLinksConsumer(TasksDbContext db, TimeProvider time, ILogger<TaskPersonLinksConsumer> logger)
{
    /// <summary>The inbox name of this consumer: "&lt;Module&gt;.&lt;Class&gt;". Do not change it later.</summary>
    public const string InboxName = "Tasks.TaskPersonLinksConsumer";

    public async Task Consume(PersonDeleted message, CancellationToken cancellationToken)
    {
        if (!await db.TryReceiveAsync(message.EventId, InboxName, cancellationToken))
        {
            return; // Handled already.
        }
        var target = message.PersonId.ToString("D");
        var links = await db.Links
            .Where(l => l.Kind == TaskLinkKinds.PeoplePerson && l.TargetId == target)
            .ToListAsync(cancellationToken);
        if (links.Count > 0)
        {
            var taskIds = links.Select(l => l.TaskId).Distinct().ToList();
            var tasks = await db.Tasks.Where(t => taskIds.Contains(t.Id)).ToListAsync(cancellationToken);
            var now = time.GetUtcNow();
            db.Links.RemoveRange(links);
            foreach (var task in tasks)
            {
                task.UpdatedAt = now;
                db.RaiseEvent(new TaskChanged(task.Id, TaskChange.Links, task.Title, task.DueOn, TaskTags.Read(task.Tags),
                    task.Repeat, task.Status, now));
            }
            logger.LogInformation("Tasks: removed {Links} link(s) to the deleted person {PersonId}", links.Count, message.PersonId);
        }
        // The inbox row commits also without a change.
        await db.SaveChangesAsync(cancellationToken);
    }
}
