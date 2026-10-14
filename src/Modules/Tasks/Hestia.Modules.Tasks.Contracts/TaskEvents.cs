using Hestia.Contracts;

namespace Hestia.Modules.Tasks.Contracts;

// The events of the Tasks module (D7). The module uses durable events (T38): each event is stored in the outbox of the
// "tasks" schema with the change, and the host delivers it after the commit, also after a restart.
// An event can come more than once: make each consumer idempotent with the EventId (T36, the module event inbox).
// To read more than an event carries, use ITaskQueries.

/// <summary>A task was added.</summary>
/// <param name="TaskId">The task.</param>
/// <param name="Title">The title.</param>
/// <param name="DueOn">The day the task is due, or null.</param>
/// <param name="Tags">The tags.</param>
/// <param name="Repeat">The repeat, or null.</param>
/// <param name="OccurredAt">When the task was added.</param>
public sealed record TaskCreated(
    Guid TaskId,
    string Title,
    DateOnly? DueOn,
    IReadOnlyList<string> Tags,
    TaskRepeatDto? Repeat,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>What changed in a task.</summary>
public enum TaskChange
{
    /// <summary>The user changed the details: the title, the notes, the due date, the tags, the repeat, or the order.</summary>
    Details,

    /// <summary>A link was added or removed (by the user, or because the linked document was deleted).</summary>
    Links
}

/// <summary>A task changed. The values are the ones after the change.</summary>
public sealed record TaskChanged(
    Guid TaskId,
    TaskChange Change,
    string Title,
    DateOnly? DueOn,
    IReadOnlyList<string> Tags,
    TaskRepeatDto? Repeat,
    TaskItemStatus Status,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>A task was done.</summary>
/// <param name="TaskId">The task.</param>
/// <param name="CompletionId">The completion (see <see cref="TaskCompletionDto"/>).</param>
/// <param name="Title">The title.</param>
/// <param name="DueOn">The due date that was done, or null for a task without a due date.</param>
/// <param name="NextDueOn">For a repeated task, its next due date. Null for a task without a repeat (it is now done).</param>
/// <param name="OccurredAt">When the task was done.</param>
public sealed record TaskCompleted(
    Guid TaskId,
    Guid CompletionId,
    string Title,
    DateOnly? DueOn,
    DateOnly? NextDueOn,
    DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>
/// The last "done" of a task was undone: a done task is open again, or a repeated task has its old due date again.
/// </summary>
/// <param name="TaskId">The task.</param>
/// <param name="CompletionId">The completion that was removed.</param>
/// <param name="Title">The title.</param>
/// <param name="DueOn">The due date after the undo.</param>
/// <param name="OccurredAt">When the undo happened.</param>
public sealed record TaskReopened(Guid TaskId, Guid CompletionId, string Title, DateOnly? DueOn, DateTimeOffset OccurredAt)
    : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>A task was deleted, with its completions and its links.</summary>
public sealed record TaskDeleted(Guid TaskId, string Title, DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public int Version { get; init; } = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();
}
