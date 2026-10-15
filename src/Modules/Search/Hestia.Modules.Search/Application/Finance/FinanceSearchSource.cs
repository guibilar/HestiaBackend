using Hestia.Modules.Search.Contracts;
using Hestia.Platform.Finance;

namespace Hestia.Modules.Search.Application.Finance;

/// <summary>
/// The Money hits of the search (T17): the transactions whose description has the text, newest first
/// (<c>POST /api/Transaction/Filter</c> with <c>description</c>, a read-only query), as the signed-in user (D13).
/// </summary>
public sealed class FinanceSearchSource(IFinanceClient finance) : ISearchSource
{
    public const string ModuleName = "Money";

    public string Module => ModuleName;

    /// <summary>First (D11).</summary>
    public int Order => 0;

    public async Task<SearchHits> SearchAsync(string text, int limit, CancellationToken cancellationToken)
    {
        var page = await finance.PostAsync<TransactionSearch, TransactionPage>("/api/Transaction/Filter",
            new TransactionSearch(text, 1, limit), cancellationToken);
        var hits = page.Items.Select(t => new SearchHit(
            $"money.transaction.{t.Id:D}",
            ModuleName,
            "transaction",
            t.Description,
            $"{t.Category?.Name ?? "Other"} · {t.AccountName}",
            DateOnly.FromDateTime(t.Date.Kind == DateTimeKind.Local ? t.Date.ToUniversalTime() : t.Date),
            t.Amount,
            $"/money/transactions?id={t.Id:D}")).ToList();
        return new SearchHits(hits, page.TotalCount);
    }
}

// The parts of the finance answers that the source reads (Finance.Contracts in PersonalFinanceInsightsBackend).

/// <summary>The body of <c>POST /api/Transaction/Filter</c>: a part of the description, newest first.</summary>
public sealed record TransactionSearch(string Description, int Page, int PageSize);

public sealed record TransactionPage
{
    public List<Transaction> Items { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed record Transaction(Guid Id, DateTime Date, string Description, decimal Amount, CategoryReference? Category, string AccountName);

public sealed record CategoryReference(Guid Id, string Name);
