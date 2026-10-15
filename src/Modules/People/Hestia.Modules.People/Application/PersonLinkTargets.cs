using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.People.Contracts;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Finance;

namespace Hestia.Modules.People.Application;

/// <summary>
/// The things that a person can link to (<see cref="PersonLinkKinds"/>), and the check that one exists: a document through
/// <see cref="IDocumentQueries"/>, a task through <see cref="ITaskQueries"/>, a home item through <see cref="IHomeQueries"/>,
/// and a finance transaction through <see cref="IFinanceClient"/> as the signed-in user (D13).
/// </summary>
public sealed class PersonLinkTargets(IDocumentQueries documents, ITaskQueries tasks, IHomeQueries home, IFinanceClient finance)
{
    /// <summary>The kind and the id in their stored form. Does not check that the thing exists.</summary>
    /// <exception cref="InvalidInputException">An unknown kind, or an id that this kind cannot have.</exception>
    public static (string Kind, string TargetId) Normalize(string? kind, string? targetId)
    {
        var name = kind?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw new InvalidInputException("kind", $"Choose what to link to: {string.Join(", ", PersonLinkKinds.All)}.");
        }
        var known = PersonLinkKinds.All.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidInputException("kind", $"\"{name}\" is not a link kind. Use one of: {string.Join(", ", PersonLinkKinds.All)}.");
        if (!Guid.TryParse(targetId?.Trim(), out var id) || id == Guid.Empty)
        {
            throw new InvalidInputException("targetId", known switch
            {
                PersonLinkKinds.DocumentsDocument => "A document id is a GUID.",
                PersonLinkKinds.TasksTask => "A task id is a GUID.",
                PersonLinkKinds.HomeItem => "A home item id is a GUID.",
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
        var exists = linkKind switch
        {
            PersonLinkKinds.DocumentsDocument => await documents.FindAsync(id, cancellationToken) is not null,
            PersonLinkKinds.TasksTask => await tasks.FindAsync(id, cancellationToken) is not null,
            PersonLinkKinds.HomeItem => await home.FindItemAsync(id, cancellationToken) is not null,
            // Finance has no "get one transaction" endpoint. The filter with the transaction id is a read-only query, and
            // it finds only visible (not deleted) transactions (D13).
            _ => (await finance.PostAsync<TransactionFilter, TransactionPage>("/api/Transaction/Filter",
                new TransactionFilter(id), cancellationToken)).TotalCount > 0
        };
        if (!exists)
        {
            throw new InvalidInputException("targetId", linkKind switch
            {
                PersonLinkKinds.DocumentsDocument => "No document has this id.",
                PersonLinkKinds.TasksTask => "No task has this id.",
                PersonLinkKinds.HomeItem => "No home item has this id.",
                _ => "Finance has no transaction with this id."
            });
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
