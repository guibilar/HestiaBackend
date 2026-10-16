namespace Hestia.Modules.Inbox.Contracts;

/// <summary>
/// One note in the Inbox (T18): a quick thought, a link, or a to-do, kept to sort later. A note is open until the user
/// sorts it (<see cref="DoneAt"/>): into a task, the journal, or nowhere.
/// </summary>
/// <param name="SortedTo">Where the note went when it was sorted, for example <c>Task</c> or <c>Journal</c>; null for none.</param>
/// <param name="Source">How the note came in: <c>Note</c> (written) or <c>Email</c> (T102).</param>
/// <param name="From">The sender of an email note; null for a written note.</param>
/// <param name="DocumentIds">The documents made from the attachments of an email (T102); empty for none.</param>
public sealed record InboxItemDto(
    Guid Id,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DoneAt,
    string? SortedTo,
    InboxSource Source = InboxSource.Note,
    string? From = null,
    IReadOnlyList<Guid>? DocumentIds = null);

/// <summary>How a note came into the Inbox.</summary>
public enum InboxSource
{
    /// <summary>The user wrote it (Capture, the Inbox page, a share on the phone).</summary>
    Note,

    /// <summary>An email to the Inbox mailbox (T102). Its attachments are documents.</summary>
    Email
}

/// <summary>The body of <c>POST /api/inbox</c> and <c>PUT /api/inbox/{id}</c>: the whole text of the note.</summary>
public sealed record InboxTextRequest(string? Text);

/// <summary>The body of <c>POST /api/inbox/{id}/done</c>: where the note went (optional).</summary>
public sealed record InboxDoneRequest(string? SortedTo = null);

/// <summary>The limits of the Inbox module, for the clients.</summary>
public static class InboxLimits
{
    public const int MaxTextLength = 4000;

    public const int MaxSortedToLength = 50;

    public const int MaxFromLength = 320;

    public const int MaxMessageIdLength = 500;
}
