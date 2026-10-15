using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.People.Contracts;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Finance;

namespace Hestia.Modules.Documents.Application;

/// <summary>
/// The things that a document can link to (<see cref="DocumentLinkKinds"/>), and the check that one exists: a finance
/// transaction through finance with the signed-in user (<see cref="IFinanceClient"/>, D13), and a home item through
/// <see cref="IHomeQueries"/> (T22), and a person through <see cref="IPeopleQueries"/> (T93). The module never reads the finance database or the tables of another module.
/// </summary>
public sealed class LinkTargets(IFinanceClient finance, IHomeQueries home, IPeopleQueries people)
{
    /// <summary>The kind and the id in their stored form. Does not check that the thing exists.</summary>
    /// <exception cref="InvalidInputException">An unknown kind, or an id that this kind cannot have.</exception>
    public static (string Kind, string TargetId) Normalize(string? kind, string? targetId)
    {
        var name = kind?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw new InvalidInputException("kind", $"Choose what to link to: {string.Join(", ", DocumentLinkKinds.All)}.");
        }
        var known = DocumentLinkKinds.All.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidInputException("kind", $"\"{name}\" is not a link kind. Use one of: {string.Join(", ", DocumentLinkKinds.All)}.");
        if (!Guid.TryParse(targetId?.Trim(), out var id) || id == Guid.Empty)
        {
            throw new InvalidInputException("targetId", known switch
            {
                DocumentLinkKinds.HomeItem => "A home item id is a GUID.",
                DocumentLinkKinds.PeoplePerson => "A person id is a GUID.",
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
        if (linkKind == DocumentLinkKinds.HomeItem)
        {
            if (await home.FindItemAsync(Guid.Parse(target), cancellationToken) is null)
            {
                throw new InvalidInputException("targetId", "No home item has this id.");
            }
            return (linkKind, target);
        }
        if (linkKind == DocumentLinkKinds.PeoplePerson)
        {
            if (await people.FindPersonAsync(Guid.Parse(target), cancellationToken) is null)
            {
                throw new InvalidInputException("targetId", "No person has this id.");
            }
            return (linkKind, target);
        }

        // Finance has no "get one transaction" endpoint. The filter with the transaction id is a read-only query, and
        // it finds only visible (not deleted) transactions.
        var page = await finance.PostAsync<TransactionFilter, TransactionPage>("/api/Transaction/Filter",
            new TransactionFilter(Guid.Parse(target)), cancellationToken);
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
