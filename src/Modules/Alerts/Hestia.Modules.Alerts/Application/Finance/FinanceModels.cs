namespace Hestia.Modules.Alerts.Application.Finance;

// The parts of the finance answers that the finance alert rules read (Finance.Contracts in PersonalFinanceInsightsBackend).
// JSON in camel case, enums by name. Fields that the rules do not use are left out; unknown fields are ignored.
// The Today module has its own copy of the reports that its tiles read: each module keeps its own view of finance.

/// <summary><c>POST /api/Budget/Filter</c> (a finance read-only query).</summary>
public sealed record BudgetPage
{
    public List<Budget> Items { get; init; } = [];
}

/// <param name="TotalAmount">The limit of the budget in its current period.</param>
/// <param name="TotalRemaining">The limit less the spending (below zero when the spending is over the limit).</param>
public sealed record Budget(Guid Id, string Name, decimal PercentageUsed, decimal TotalAmount = 0, decimal TotalRemaining = 0);

/// <summary>The body of <c>POST /api/Budget/Filter</c>: all active budgets, as the web dashboard asks.</summary>
public sealed record BudgetFilter(string Status, int Page, int PageSize)
{
    public static readonly BudgetFilter AllActive = new("Active", 1, 1000);
}

/// <summary><c>GET /api/Reports/uncategorised</c>.</summary>
public sealed record UncategorisedReport(int TransactionCount);

/// <summary><c>GET /api/Reports/recurring</c>.</summary>
public sealed record RecurringReport
{
    public List<RecurringPayment> Payments { get; init; } = [];
}

/// <param name="Status">"Active", "Late", or "Stopped".</param>
public sealed record RecurringPayment(string Name, string Status, PriceChange? PriceChange);

/// <param name="Since">The day of the first charge at the new price (midnight UTC).</param>
public sealed record PriceChange(decimal From, decimal To, DateTime Since);

/// <summary>One item of <c>GET /api/Accounts</c>.</summary>
/// <param name="Format">The statement format: "Manual" for an account without imports.</param>
/// <param name="LastTransactionDate">The day of the latest transaction (midnight UTC).</param>
/// <param name="LastImportedAt">When a statement was last imported (UTC).</param>
public sealed record Account(Guid Id, string Name, string Format, DateTime? LastTransactionDate, DateTime? LastImportedAt,
    AccountBalance? Balance);

/// <param name="Date">The day of the balance (midnight UTC).</param>
public sealed record AccountBalance(DateTime Date, decimal Amount);
