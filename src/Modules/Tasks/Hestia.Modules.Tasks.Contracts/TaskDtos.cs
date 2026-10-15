namespace Hestia.Modules.Tasks.Contracts;

/// <summary>The state of a task. A repeated task stays <see cref="Open"/>: "done" moves its due date (T19).</summary>
public enum TaskItemStatus
{
    /// <summary>The task is to do.</summary>
    Open,

    /// <summary>The task is done. Only a task without a repeat can be done.</summary>
    Done
}

/// <summary>The unit of a repeat: "every 4 days", "every 12 months".</summary>
public enum RepeatUnit
{
    Days,
    Weeks,
    Months,
    Years
}

/// <summary>How the next due date of a repeated task is found.</summary>
public enum RepeatMode
{
    /// <summary>
    /// A fixed schedule from the due date, for example the 1st of each month. A task that is done late moves to the first
    /// date of the schedule after the day it was done.
    /// </summary>
    FromDueDate,

    /// <summary>From the day the task was done, for example "water the plants 4 days after the last time".</summary>
    FromCompletion
}

/// <summary>The repeat of a task.</summary>
/// <param name="Every">How many units between two due dates (1 or more).</param>
/// <param name="Unit">Days, weeks, months, or years.</param>
/// <param name="Mode">From the due date (a fixed schedule) or from the day the task was done.</param>
public sealed record TaskRepeatDto(int Every, RepeatUnit Unit, RepeatMode Mode);

/// <summary>A link from a task to something outside the Tasks module, for example a document.</summary>
/// <param name="Id">The link.</param>
/// <param name="Kind">What the link points to, for example <c>Documents.Document</c> (<see cref="TaskLinkKinds"/>).</param>
/// <param name="TargetId">The id of the thing, as text.</param>
/// <param name="CreatedAt">When the link was made.</param>
public sealed record TaskLinkDto(Guid Id, string Kind, string TargetId, DateTimeOffset CreatedAt);

/// <summary>One time that a task was done.</summary>
/// <param name="Id">The completion.</param>
/// <param name="DoneAt">When the task was done.</param>
/// <param name="DueOn">The due date that was done, or null for a task without a due date.</param>
public sealed record TaskCompletionDto(Guid Id, DateTimeOffset DoneAt, DateOnly? DueOn);

/// <summary>A task in a list.</summary>
/// <param name="Id">The task.</param>
/// <param name="Title">The title.</param>
/// <param name="Notes">Free text, or null.</param>
/// <param name="DueOn">The day the task is due, or null for "some day". A repeated task always has one.</param>
/// <param name="Tags">The tags, in lower case.</param>
/// <param name="Status">Open or done.</param>
/// <param name="DoneAt">When the task was done (a task without a repeat), else null.</param>
/// <param name="Repeat">The repeat, or null.</param>
/// <param name="SortOrder">The manual order inside a day (lower first), or null.</param>
/// <param name="LastDoneAt">When the task was last done (also for a repeated task), or null.</param>
/// <param name="CreatedAt">When the task was added.</param>
/// <param name="UpdatedAt">When the task last changed.</param>
/// <param name="Links">The links, oldest first.</param>
public sealed record TaskSummaryDto(
    Guid Id,
    string Title,
    string? Notes,
    DateOnly? DueOn,
    IReadOnlyList<string> Tags,
    TaskItemStatus Status,
    DateTimeOffset? DoneAt,
    TaskRepeatDto? Repeat,
    int? SortOrder,
    DateTimeOffset? LastDoneAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<TaskLinkDto> Links);

/// <summary>One task, with its last completions.</summary>
/// <param name="Id">The task.</param>
/// <param name="Title">The title.</param>
/// <param name="Notes">Free text, or null.</param>
/// <param name="DueOn">The day the task is due, or null.</param>
/// <param name="Tags">The tags, in lower case.</param>
/// <param name="Status">Open or done.</param>
/// <param name="DoneAt">When the task was done (a task without a repeat), else null.</param>
/// <param name="Repeat">The repeat, or null.</param>
/// <param name="SortOrder">The manual order inside a day, or null.</param>
/// <param name="LastDoneAt">When the task was last done, or null.</param>
/// <param name="CreatedAt">When the task was added.</param>
/// <param name="UpdatedAt">When the task last changed.</param>
/// <param name="Links">The links, oldest first.</param>
/// <param name="Completions">The last 10 completions, newest first.</param>
public sealed record TaskDto(
    Guid Id,
    string Title,
    string? Notes,
    DateOnly? DueOn,
    IReadOnlyList<string> Tags,
    TaskItemStatus Status,
    DateTimeOffset? DoneAt,
    TaskRepeatDto? Repeat,
    int? SortOrder,
    DateTimeOffset? LastDoneAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<TaskLinkDto> Links,
    IReadOnlyList<TaskCompletionDto> Completions);

/// <summary>A repeat in a request. <paramref name="Mode"/> is <see cref="RepeatMode.FromDueDate"/> when it is not sent.</summary>
public sealed record TaskRepeatRequest(int? Every, RepeatUnit? Unit, RepeatMode? Mode);

/// <summary>A new task (<c>POST /api/tasks</c>). Only the title is required.</summary>
/// <param name="Title">The title (at most 200 characters).</param>
/// <param name="Notes">Free text.</param>
/// <param name="DueOn">The day the task is due. A repeated task without one is due today.</param>
/// <param name="Tags">The tags.</param>
/// <param name="Repeat">The repeat, or null.</param>
/// <param name="SortOrder">The manual order inside a day, or null.</param>
/// <param name="Links">Links to make at once, for example to a document.</param>
public sealed record CreateTaskRequest(
    string? Title,
    string? Notes = null,
    DateOnly? DueOn = null,
    IReadOnlyList<string>? Tags = null,
    TaskRepeatRequest? Repeat = null,
    int? SortOrder = null,
    IReadOnlyList<CreateTaskLinkRequest>? Links = null);

/// <summary>
/// The new details of a task (<c>PUT /api/tasks/{id}</c>). All values replace the old ones: a value that is not sent
/// becomes empty. The status, the completions, and the links do not change.
/// </summary>
public sealed record UpdateTaskRequest(
    string? Title,
    string? Notes = null,
    DateOnly? DueOn = null,
    IReadOnlyList<string>? Tags = null,
    TaskRepeatRequest? Repeat = null,
    int? SortOrder = null);

/// <summary>A new link (<c>POST /api/tasks/{id}/links</c>).</summary>
/// <param name="Kind">What to link to: <c>Documents.Document</c>, <c>Money.Transaction</c>, <c>Home.Item</c>, or <c>People.Person</c>.</param>
/// <param name="TargetId">The id of the thing, for example the document id.</param>
public sealed record CreateTaskLinkRequest(string? Kind, string? TargetId);

/// <summary>The counts of open tasks, for the badges of the clients (<c>GET /api/tasks/summary</c>).</summary>
/// <param name="Date">Today, in the time zone of the module (<c>Tasks:TimeZone</c>).</param>
/// <param name="DueToday">Open tasks due today.</param>
/// <param name="Overdue">Open tasks due before today.</param>
/// <param name="Upcoming">Open tasks due after today, in the next 7 days (<c>Tasks:UpcomingDays</c>).</param>
public sealed record TaskCountsDto(DateOnly Date, int DueToday, int Overdue, int Upcoming);

/// <summary>The kinds of link that the Tasks module knows.</summary>
public static class TaskLinkKinds
{
    /// <summary>A document of the Documents module. The target id is the document id (a GUID).</summary>
    public const string DocumentsDocument = "Documents.Document";

    /// <summary>A finance transaction. The target id is the transaction id (a GUID).</summary>
    public const string MoneyTransaction = "Money.Transaction";

    /// <summary>An item of the Home module (T22): the task is maintenance of that item. The target id is the item id.</summary>
    public const string HomeItem = "Home.Item";

    /// <summary>A person of the People module (T93), for example "Call Marco". The target id is the person id.</summary>
    public const string PeoplePerson = "People.Person";

    public static readonly IReadOnlyList<string> All = [DocumentsDocument, MoneyTransaction, HomeItem, PeoplePerson];
}
