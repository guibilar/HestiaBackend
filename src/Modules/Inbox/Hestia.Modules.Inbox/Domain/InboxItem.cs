namespace Hestia.Modules.Inbox.Domain;

/// <summary>A note in the Inbox. Open while <see cref="DoneAt"/> is null.</summary>
public class InboxItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DoneAt { get; set; }

    /// <summary>Where the note went when it was sorted (free text, for example "Task").</summary>
    public string? SortedTo { get; set; }
}
