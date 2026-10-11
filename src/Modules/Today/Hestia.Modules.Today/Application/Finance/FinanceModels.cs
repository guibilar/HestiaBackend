namespace Hestia.Modules.Today.Application.Finance;

// The parts of the finance answers that the finance cards read (Finance.Contracts in PersonalFinanceInsightsBackend).
// JSON in camel case, enums by name. Fields that the cards do not use are left out; unknown fields are ignored.

/// <summary><c>GET /api/Reports/current-month-categorized-graph</c> and <c>monthly-categorized-graph</c>.</summary>
public sealed record CategorizedReport
{
    public List<MonthTotal> MonthlyData { get; init; } = [];
    public decimal AverageMonthlyTotal { get; init; }

    /// <summary>True when no month of the year is over yet (January): the average is of the running month.</summary>
    public bool AverageIncludesCurrentMonth { get; init; }

    /// <summary>Current-month report only: last month up to the same day of the month.</summary>
    public decimal? PreviousMonthToDateTotal { get; init; }
}

public sealed record MonthTotal(string Month, decimal Total);

/// <summary><c>GET /api/Reports/cash-flow</c>.</summary>
public sealed record CashFlowReport
{
    public List<CashFlowMonth> Months { get; init; } = [];
}

/// <param name="Month">"Oct 2026".</param>
public sealed record CashFlowMonth(string Month, decimal Income, decimal Spending, decimal Net);

/// <summary><c>GET /api/Reports/net-worth</c>.</summary>
public sealed record NetWorthReport
{
    public decimal NetWorth { get; init; }

    /// <summary>Oldest first; the current month is the last.</summary>
    public List<NetWorthMonth> Months { get; init; } = [];
}

public sealed record NetWorthMonth(string Month, decimal NetWorth, int AccountsCounted);

/// <summary><c>POST /api/Budget/Filter</c> (a finance read-only query).</summary>
public sealed record BudgetPage
{
    public List<Budget> Items { get; init; } = [];
}

public sealed record Budget(Guid Id, string Name, decimal PercentageUsed);

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
