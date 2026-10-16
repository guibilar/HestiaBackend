using Hestia.Modules.Inbox.Contracts;

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

    /// <summary>How the note came in (T102): written, or an email.</summary>
    public InboxSource Source { get; set; }

    /// <summary>The sender of an email note, for example <c>Hydro &lt;bills@hydro.example&gt;</c>.</summary>
    public string? From { get; set; }

    /// <summary>The Message-ID of an email, so the same email never makes two notes.</summary>
    public string? MessageId { get; set; }

    /// <summary>The documents made from the attachments of an email, stored as <c>|id|id|</c>, or empty.</summary>
    public string DocumentIds { get; set; } = string.Empty;
}
