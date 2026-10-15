namespace Hestia.Modules.People.Contracts;

/// <summary>
/// How other modules read the people without the People tables (for example a later link check, or Timeline). The People
/// module registers the implementation. It reads only the People tables.
/// </summary>
public interface IPeopleQueries
{
    /// <summary>The person, or null when no person has this id.</summary>
    Task<PersonSummaryDto?> FindPersonAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The people whose links point to one thing, by name. <paramref name="kind"/> is a link kind
    /// (<see cref="PersonLinkKinds"/>). An unknown kind gives an empty list.
    /// </summary>
    Task<IReadOnlyList<PersonSummaryDto>> ListByLinkAsync(string kind, string targetId, CancellationToken cancellationToken = default);
}
