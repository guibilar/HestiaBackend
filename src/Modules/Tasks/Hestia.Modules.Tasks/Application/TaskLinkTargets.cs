using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.People.Contracts;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Finance;

namespace Hestia.Modules.Tasks.Application;

/// <summary>
/// The things that a task can link to (<see cref="TaskLinkKinds"/>), and the check that one exists: a document through
/// <see cref="IDocumentQueries"/>, a home item through <see cref="IHomeQueries"/> (T22), a person through
/// <see cref="IPeopleQueries"/> (T93), a finance transaction through
/// <see cref="IFinanceClient"/> as the signed-in user (D13). The module never reads the tables of another module or the
/// finance database.
/// </summary>
public sealed class TaskLinkTargets(IDocumentQueries documents, IHomeQueries home, IPeopleQueries people, IFinanceClient finance)
{
    /// <summary>The kind and the id in their stored form. Does not check that the thing exists.</summary>
    /// <exception cref="InvalidInputException">An unknown kind, or an id that this kind cannot have.</exception>
    public static (string Kind, string TargetId) Normalize(string? kind, string? targetId)
    {
        var name = kind?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw new InvalidInputException("kind", $"Choose what to link to: {string.Join(", ", TaskLinkKinds.All)}.");
        }
        var known = TaskLinkKinds.All.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidInputException("kind", $"\"{name}\" is not a link kind. Use one of: {string.Join(", ", TaskLinkKinds.All)}.");
        if (!Guid.TryParse(targetId?.Trim(), out var id) || id == Guid.Empty)
        {
            throw new InvalidInputException("targetId", known switch
            {
                TaskLinkKinds.DocumentsDocument => "A document id is a GUID.",
                TaskLinkKinds.HomeItem => "A home item id is a GUID.",
                TaskLinkKinds.PeoplePerson => "A person id is a GUID.",
                _ => "A finance transaction id is a GUID."
            });
        }
        return (known, id.ToString("D"));
    }

    /// <summary>The kind and the id in their stored form, after a check that the thing exists.</summary>
    /// <exception cref="InvalidInputException">An unknown kind or id, or no such thing.</exception>
    /// <exception cref="FinanceRequestException">Finance did not answer (the client gets 502).</exception>
    public async Task<(string Kind, string TargetId)> CheckAsync(string? kind, string? targetId, CancellationToken cancellationToken)
    {
        var (linkKind, target) = Normalize(kind, targetId);
        var id = Guid.Parse(target);
        if (linkKind == TaskLinkKinds.DocumentsDocument)
        {
            if (await documents.FindAsync(id, cancellationToken) is null)
            {
                throw new InvalidInputException("targetId", "No document has this id.");
            }
            return (linkKind, target);
        }
        if (linkKind == TaskLinkKinds.HomeItem)
        {
            if (await home.FindItemAsync(id, cancellationToken) is null)
            {
                throw new InvalidInputException("targetId", "No home item has this id.");
            }
            return (linkKind, target);
        }
        if (linkKind == TaskLinkKinds.PeoplePerson)
        {
            if (await people.FindPersonAsync(id, cancellationToken) is null)
            {
                throw new InvalidInputException("targetId", "No person has this id.");
            }
            return (linkKind, target);
        }

        // Finance has no "get one transaction" endpoint. The filter with the transaction id is a read-only query, and it
        // finds only visible (not deleted) transactions (D13).
        var page = await finance.PostAsync<TransactionFilter, TransactionPage>("/api/Transaction/Filter",
            new TransactionFilter(id), cancellationToken);
        if (page.TotalCount == 0)
        {
            throw new InvalidInputException("targetId", "Finance has no transaction with this id.");
        }
        return (linkKind, target);
    }
}

/// <summary>The finance transaction filter (finance: TransactionFilterDto), for one transaction.</summary>
internal sealed record TransactionFilter(Guid TransactionId)
{
    public int Page => 1;

    public int PageSize => 1;
}

/// <summary>The part of a finance page of transactions that the check needs.</summary>
internal sealed record TransactionPage(int TotalCount);
