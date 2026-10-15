namespace Hestia.Modules.Inbox.Contracts;

/// <summary>
/// One note in the Inbox (T18): a quick thought, a link, or a to-do, kept to sort later. A note is open until the user
/// sorts it (<see cref="DoneAt"/>): into a task, the journal, or nowhere.
/// </summary>
/// <param name="SortedTo">Where the note went when it was sorted, for example <c>Task</c> or <c>Journal</c>; null for none.</param>
public sealed record InboxItemDto(
    Guid Id,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DoneAt,
    string? SortedTo);

/// <summary>The body of <c>POST /api/inbox</c> and <c>PUT /api/inbox/{id}</c>: the whole text of the note.</summary>
public sealed record InboxTextRequest(string? Text);

/// <summary>The body of <c>POST /api/inbox/{id}/done</c>: where the note went (optional).</summary>
public sealed record InboxDoneRequest(string? SortedTo = null);

/// <summary>The limits of the Inbox module, for the clients.</summary>
public static class InboxLimits
{
    public const int MaxTextLength = 4000;

    public const int MaxSortedToLength = 50;
}
