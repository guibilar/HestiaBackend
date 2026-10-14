namespace Hestia.Modules.Tasks.Contracts;

/// <summary>
/// How other modules read tasks without the Tasks tables (for example the tasks of a document, or Timeline). The Tasks
/// module registers the implementation.
/// </summary>
public interface ITaskQueries
{
    /// <summary>The task with its last completions, or null when no task has this id.</summary>
    Task<TaskDto?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The tasks linked to one thing, in list order (due date first). <paramref name="kind"/> is a link kind
    /// (<see cref="TaskLinkKinds"/>), and <paramref name="targetId"/> its id, for example a document id.
    /// An unknown kind gives an empty list.
    /// </summary>
    Task<IReadOnlyList<TaskSummaryDto>> ListByLinkAsync(string kind, string targetId, CancellationToken cancellationToken = default);
}
