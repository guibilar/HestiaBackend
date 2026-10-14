namespace Hestia.Modules.Home.Contracts;

/// <summary>
/// How other modules read the home inventory without the Home tables (for example the link checks of Documents and
/// Tasks, or Timeline). The Home module registers the implementation. It reads only the Home tables, so a module that
/// Home calls (Documents, Tasks) can use it without a loop.
/// </summary>
public interface IHomeQueries
{
    /// <summary>The item, or null when no item has this id.</summary>
    Task<ItemSummaryDto?> FindItemAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The items whose own links point to one thing, by name. <paramref name="kind"/> is a link kind
    /// (<see cref="ItemLinkKinds"/>), and <paramref name="targetId"/> its id, for example a document id.
    /// An unknown kind gives an empty list.
    /// </summary>
    Task<IReadOnlyList<ItemSummaryDto>> ListByLinkAsync(string kind, string targetId, CancellationToken cancellationToken = default);
}
