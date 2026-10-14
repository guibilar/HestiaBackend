using System.Text.RegularExpressions;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Platform.Exceptions;

namespace Hestia.Modules.Tasks.Domain;

/// <summary>
/// The tags of a task, as in the Documents module: one text column <c>|tag1|tag2|</c>, so a filter by tag is a text
/// search for <c>|tag|</c>.
/// </summary>
public static partial class TaskTags
{
    public const int MaxTags = 20;
    public const int MaxTagLength = 50;
    public const char Separator = '|';

    /// <summary>The longest stored value: 20 tags of 50 characters, each after a separator, and the last separator.</summary>
    public const int MaxStoredLength = MaxTags * (MaxTagLength + 1) + 1;

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    /// <summary>
    /// The tags as the user gave them, cleaned: each value can hold several tags with commas between them. A tag is
    /// trimmed, in lower case, with single spaces. Empty tags and repeats are left out.
    /// </summary>
    /// <exception cref="InvalidInputException">A tag is too long or has a "|", or there are too many tags.</exception>
    public static List<string> Normalize(IEnumerable<string?>? values)
    {
        var tags = new List<string>();
        foreach (var value in values ?? [])
        {
            foreach (var part in (value ?? string.Empty).Split(','))
            {
                var tag = Spaces().Replace(part.Trim(), " ").ToLowerInvariant();
                if (tag.Length == 0 || tags.Contains(tag))
                {
                    continue;
                }
                if (tag.Contains(Separator))
                {
                    throw new InvalidInputException("tags", $"A tag cannot have the character \"{Separator}\".");
                }
                if (tag.Length > MaxTagLength)
                {
                    throw new InvalidInputException("tags", $"A tag can have at most {MaxTagLength} characters.");
                }
                tags.Add(tag);
            }
        }
        if (tags.Count > MaxTags)
        {
            throw new InvalidInputException("tags", $"A task can have at most {MaxTags} tags.");
        }
        return tags;
    }

    public static string Store(IReadOnlyCollection<string> tags) =>
        tags.Count == 0 ? string.Empty : $"{Separator}{string.Join(Separator, tags)}{Separator}";

    public static List<string> Read(string stored) =>
        stored.Split(Separator, StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>The text to search for in the stored value, to find one tag.</summary>
    public static string Pattern(string tag) => $"{Separator}{tag}{Separator}";
}

/// <summary>The details of a task that the user sets: checked and cleaned for a create and for a change.</summary>
public sealed record TaskDetails(
    string Title,
    string? Notes,
    DateOnly? DueOn,
    List<string> Tags,
    TaskRepeatDto? Repeat,
    int? SortOrder)
{
    /// <summary>The most units in one repeat step (for example 1000 days).</summary>
    public const int MaxEvery = 1000;

    /// <exception cref="InvalidInputException">A value cannot be used.</exception>
    /// <param name="title">The title.</param>
    /// <param name="notes">The notes.</param>
    /// <param name="dueOn">The due date. A repeated task without one is due on <paramref name="today"/>.</param>
    /// <param name="tags">The tags.</param>
    /// <param name="repeat">The repeat, or null.</param>
    /// <param name="sortOrder">The manual order, or null.</param>
    /// <param name="today">Today, in the time zone of the module.</param>
    public static TaskDetails Create(string? title, string? notes, DateOnly? dueOn, IEnumerable<string?>? tags,
        TaskRepeatRequest? repeat, int? sortOrder, DateOnly today)
    {
        var cleanTitle = title?.Trim() ?? string.Empty;
        if (cleanTitle.Length == 0)
        {
            throw new InvalidInputException("title", "Enter a title.");
        }
        if (cleanTitle.Length > TaskItem.MaxTitleLength)
        {
            throw new InvalidInputException("title", $"The title can have at most {TaskItem.MaxTitleLength} characters.");
        }
        var cleanNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (cleanNotes?.Length > TaskItem.MaxNotesLength)
        {
            throw new InvalidInputException("notes", $"The notes can have at most {TaskItem.MaxNotesLength} characters.");
        }
        var cleanRepeat = ParseRepeat(repeat);
        // A repeated task has a due date: without one, the schedule starts today.
        var due = cleanRepeat is null ? dueOn : dueOn ?? today;
        return new TaskDetails(cleanTitle, cleanNotes, due, TaskTags.Normalize(tags), cleanRepeat, sortOrder);
    }

    /// <exception cref="InvalidInputException">The repeat cannot be used.</exception>
    public static TaskRepeatDto? ParseRepeat(TaskRepeatRequest? repeat)
    {
        if (repeat is null)
        {
            return null;
        }
        if (repeat.Every is not { } every || every < 1 || every > MaxEvery)
        {
            throw new InvalidInputException("repeat.every", $"Repeat every 1 to {MaxEvery} units.");
        }
        if (repeat.Unit is not { } unit || !Enum.IsDefined(unit))
        {
            throw new InvalidInputException("repeat.unit", $"Choose the unit of the repeat: {string.Join(", ", Enum.GetNames<RepeatUnit>())}.");
        }
        var mode = repeat.Mode ?? RepeatMode.FromDueDate;
        if (!Enum.IsDefined(mode))
        {
            throw new InvalidInputException("repeat.mode", $"Choose the mode of the repeat: {string.Join(", ", Enum.GetNames<RepeatMode>())}.");
        }
        return new TaskRepeatDto(every, unit, mode);
    }

    /// <summary>
    /// Applies the details to a task. A new due date or a new repeat starts the schedule again at that day. The same
    /// due date and repeat keep the day of the schedule (for example 31 while the due date is Feb 28).
    /// </summary>
    public void ApplyTo(TaskItem task)
    {
        var sameSchedule = task.DueOn == DueOn && task.Repeat == Repeat;
        task.Title = Title;
        task.Notes = Notes;
        task.Tags = TaskTags.Store(Tags);
        task.SortOrder = SortOrder;
        task.RepeatEvery = Repeat?.Every;
        task.RepeatUnit = Repeat?.Unit;
        task.RepeatMode = Repeat?.Mode;
        task.RepeatDay = Repeat is null ? null : sameSchedule && task.RepeatDay is not null ? task.RepeatDay : DueOn?.Day;
        task.DueOn = DueOn;
    }
}
