using Hestia.Modules.Tasks.Contracts;
using Hestia.Platform.Domain;

namespace Hestia.Modules.Tasks.Domain;

/// <summary>
/// A task: a to-do or a repeated home task. Not "Task", so the name does not clash with
/// <see cref="System.Threading.Tasks.Task"/>.
/// </summary>
public class TaskItem : BaseEntity
{
    public const int MaxTitleLength = 200;
    public const int MaxNotesLength = 4000;

    public string Title { get; set; } = string.Empty;
    public string? Notes { get; set; }

    /// <summary>The day the task is due, in the time zone of the module, or null. A repeated task always has one.</summary>
    public DateOnly? DueOn { get; set; }

    /// <summary>The tags, stored as <c>|tag1|tag2|</c> (see <see cref="TaskTags"/>), or empty.</summary>
    public string Tags { get; set; } = string.Empty;

    public TaskItemStatus Status { get; set; }

    /// <summary>When a task without a repeat was done. Null while it is open.</summary>
    public DateTimeOffset? DoneAt { get; set; }

    /// <summary>The manual order inside a day: lower first. Null comes after all numbers.</summary>
    public int? SortOrder { get; set; }

    /// <summary>The repeat: every <see cref="RepeatEvery"/> <see cref="RepeatUnit"/>. All three are set, or none.</summary>
    public int? RepeatEvery { get; set; }

    public RepeatUnit? RepeatUnit { get; set; }
    public RepeatMode? RepeatMode { get; set; }

    /// <summary>
    /// The day of the month of the schedule (1 to 31), for a repeat in months or years. A short month moves the due
    /// date to its last day, and the next month goes back to this day (Jan 31, Feb 28, Mar 31).
    /// </summary>
    public int? RepeatDay { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<TaskCompletion> Completions { get; set; } = [];
    public List<TaskLink> Links { get; set; } = [];

    public TaskRepeatDto? Repeat =>
        RepeatEvery is { } every && RepeatUnit is { } unit && RepeatMode is { } mode ? new TaskRepeatDto(every, unit, mode) : null;
}

/// <summary>
/// One time that a task was done. For a repeated task, it keeps the due date that was done, so an undo can put it back,
/// and the clients can show "last done" and the history.
/// </summary>
public class TaskCompletion : BaseEntity
{
    public Guid TaskId { get; set; }
    public DateTimeOffset DoneAt { get; set; }

    /// <summary>The due date that was done, or null for a task without a due date.</summary>
    public DateOnly? DueOn { get; set; }
}

/// <summary>
/// A link from a task to something outside the module. The kind and the id are text, as in the Documents module, so
/// later modules can use the same table.
/// </summary>
public class TaskLink : BaseEntity
{
    public const int MaxKindLength = 50;
    public const int MaxTargetIdLength = 100;

    public Guid TaskId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
