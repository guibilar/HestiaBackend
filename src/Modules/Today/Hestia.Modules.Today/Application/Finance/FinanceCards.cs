using System.Globalization;
using Hestia.Modules.Today.Contracts;

namespace Hestia.Modules.Today.Application.Finance;

/// <summary>
/// The finance cards of Today, from the finance reports: the four headline tiles and the "Needs attention" items.
/// The rules are the same as the web dashboard (<c>FinanceTilesComponent</c>, <c>dashboard-stats.ts</c>,
/// <c>attention.ts</c>) and the Android app (<c>core/logic/Attention.kt</c>). A report that did not load is null.
/// </summary>
public static class FinanceCards
{
    public const string Module = "Money";

    /// <summary>The text of a tile whose report did not load.</summary>
    public const string NotLoaded = "Could not load this";

    /// <summary>More days than this without a statement import is worth a mention.</summary>
    public const int ImportReminderDays = 7;

    /// <summary>Price changes this recent are worth a mention.</summary>
    public const int PriceChangeDays = 45;

    public const string BudgetsLink = "/money/budgets";
    public const string NetWorthLink = "/money/net-worth";
    /// <summary>The Transactions page with the statement import open (T57).</summary>
    public const string TransactionsImportLink = "/money/transactions#import";
    public const string SubscriptionsLink = "/money/subscriptions";
    public const string UnsortedLink = "/money/rules#unsorted";

    /// <summary>Everything that the finance cards read. Null: that report did not load.</summary>
    public sealed record Inputs(
        CategorizedReport? Month,
        CategorizedReport? Year,
        CashFlowReport? CashFlow,
        NetWorthReport? NetWorth,
        BudgetPage? Budgets,
        UncategorisedReport? Uncategorised,
        RecurringReport? Recurring,
        IReadOnlyList<Account>? Accounts);

    /// <summary>The tiles, then the attention items.</summary>
    public static IReadOnlyList<TodayCard> Make(Inputs inputs, DateOnly today, TimeZoneInfo timeZone) =>
    [
        SpentTile(inputs.Month, inputs.Year, today),
        LeftOverTile(inputs.CashFlow, today),
        NetWorthTile(inputs.NetWorth),
        BudgetsTile(inputs.Budgets),
        .. AttentionItems(inputs, today, timeZone)
    ];

    // ---- Tiles ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Spent this month, against a usual month by now. Until a month of the year is over, against last month up to
    /// the same day.
    /// </summary>
    public static TodayCard SpentTile(CategorizedReport? month, CategorizedReport? year, DateOnly today)
    {
        const string title = "Spent this month";
        if (month is null)
        {
            return Unavailable("spent-this-month", "spent", title, link: null);
        }
        var spent = month.MonthlyData.FirstOrDefault()?.Total ?? 0;
        var hint = UsualHint(spent, year, today) ?? PreviousMonthHint(spent, month.PreviousMonthToDateTotal, today);
        return Tile("spent-this-month", "spent", title, TodayValue.Money(spent), hint, link: null);
    }

    /// <summary>
    /// The monthly average spread evenly over the month, up to today, against what was spent. Null while no month of
    /// the year is over (the average is then of this month itself), or without an average.
    /// </summary>
    private static Hint? UsualHint(decimal spent, CategorizedReport? year, DateOnly today)
    {
        if (year is null || year.AverageMonthlyTotal <= 0 || year.AverageIncludesCurrentMonth)
        {
            return null;
        }
        var expected = UsualByNow(year.AverageMonthlyTotal, today);
        var difference = spent - expected;
        var over = difference > 0;
        return new Hint(
            over ? "{difference} over a usual month by now" : "{difference} under a usual month by now",
            over ? TodaySeverity.Bad : TodaySeverity.Good,
            new Dictionary<string, TodayValue> { ["difference"] = TodayValue.Money(Math.Abs(difference)) },
            over ? TodayTrend.Up : TodayTrend.Down);
    }

    /// <summary>What a usual month (its average) has cost by today, spread evenly over its days.</summary>
    public static decimal UsualByNow(decimal monthlyAverage, DateOnly today) =>
        monthlyAverage * today.Day / DateTime.DaysInMonth(today.Year, today.Month);

    /// <summary>Like for like: last month up to the same day, not all of it against a month in progress.</summary>
    private static Hint? PreviousMonthHint(decimal spent, decimal? previous, DateOnly today)
    {
        if (PercentChange(spent, previous) is not { } change)
        {
            return null;
        }
        var more = change >= 0;
        return new Hint(
            more ? "{change} more than {period}" : "{change} less than {period}",
            change > 0 ? TodaySeverity.Bad : TodaySeverity.Good,
            new Dictionary<string, TodayValue>
            {
                ["change"] = TodayValue.Percent(Math.Round(Math.Abs(change), MidpointRounding.AwayFromZero)),
                ["period"] = TodayValue.Plain(PreviousMonthToDateLabel(today))
            },
            more ? TodayTrend.Up : TodayTrend.Down);
    }

    /// <summary>Percentage change from <paramref name="previous"/>; null against nothing, or against refunds outweighing spending.</summary>
    public static decimal? PercentChange(decimal current, decimal? previous) =>
        previous is not { } before || before <= 0 ? null : (current - before) / before * 100;

    /// <summary>
    /// The part of last month that this month is compared with: day 1 up to today's day of the month, or all of it when
    /// it is shorter. "Aug 1–5" on September 5th, "Feb 1–28" on March 30th, "Aug 1" on September 1st.
    /// </summary>
    public static string PreviousMonthToDateLabel(DateOnly today)
    {
        var previous = today.AddDays(1 - today.Day).AddMonths(-1);
        var lastDay = Math.Min(today.Day, DateTime.DaysInMonth(previous.Year, previous.Month));
        var name = previous.ToString("MMM", CultureInfo.InvariantCulture);
        return lastDay == 1 ? $"{name} 1" : $"{name} 1–{lastDay}";
    }

    /// <summary>Income less spending this month. Before payday, spending alone is not a warning.</summary>
    public static TodayCard LeftOverTile(CashFlowReport? cashFlow, DateOnly today)
    {
        const string title = "Left over this month";
        if (cashFlow is null)
        {
            return Unavailable("left-over-this-month", "left-over", title, link: null);
        }
        var label = MonthLabel(today);
        var month = cashFlow.Months.FirstOrDefault(m => m.Month == label);
        var hint = month switch
        {
            null => new Hint("No income or spending yet", TodaySeverity.Neutral),
            { Income: 0m } => new Hint("No income yet this month", TodaySeverity.Neutral),
            { Net: >= 0m } => new Hint("Income less spending so far", TodaySeverity.Good),
            _ => new Hint("More went out than came in", TodaySeverity.Bad)
        };
        return Tile("left-over-this-month", "left-over", title, month is null ? null : TodayValue.Money(month.Net), hint, link: null);
    }

    /// <summary>Net worth today, and how it moved since the end of last month.</summary>
    public static TodayCard NetWorthTile(NetWorthReport? report)
    {
        const string title = "Net worth";
        if (report is null)
        {
            return Unavailable("net-worth", "net-worth", title, NetWorthLink);
        }
        Hint? hint = null;
        if (report.Months.Count == 0)
        {
            // Nothing to go on until a balance is entered (or a statement brings one).
            hint = new Hint("Enter a balance to start", TodaySeverity.Neutral);
        }
        else if (NetWorthChange(report.Months) is { } change)
        {
            hint = new Hint(
                change >= 0 ? "+{change} since last month" : "−{change} since last month",
                change > 0 ? TodaySeverity.Good : change < 0 ? TodaySeverity.Bad : TodaySeverity.Neutral,
                new Dictionary<string, TodayValue> { ["change"] = TodayValue.Money(Math.Abs(change)) },
                change > 0 ? TodayTrend.Up : change < 0 ? TodayTrend.Down : null);
        }
        return Tile("net-worth", "net-worth", title, TodayValue.Money(report.NetWorth), hint, NetWorthLink);
    }

    /// <summary>
    /// How net worth moved since the end of last month; null when that month is not known, or was known for fewer
    /// accounts than today (an account whose history starts later would read as a jump).
    /// </summary>
    public static decimal? NetWorthChange(IReadOnlyList<NetWorthMonth> months)
    {
        if (months.Count < 2)
        {
            return null;
        }
        var (before, now) = (months[^2], months[^1]);
        return before.AccountsCounted < now.AccountsCounted ? null : now.NetWorth - before.NetWorth;
    }

    /// <summary>How many active budgets are at or over their limit.</summary>
    public static TodayCard BudgetsTile(BudgetPage? budgets)
    {
        const string title = "Budgets";
        if (budgets is null)
        {
            return Unavailable("budgets", "budgets", title, BudgetsLink);
        }
        var total = budgets.Items.Count;
        var over = budgets.Items.Count(IsOver);
        var value = TodayValue.Plain(total == 0 ? "None yet" : $"{over} of {total} over");
        var hint = total == 0
            ? new Hint("Create one to track a limit", TodaySeverity.Neutral)
            : over == 0
                ? new Hint("All within limits", TodaySeverity.Good)
                : new Hint("Review your budgets", TodaySeverity.Bad);
        return Tile("budgets", "budgets", title, value, hint, BudgetsLink);
    }

    private static bool IsOver(Budget budget) => budget.PercentageUsed >= 100;

    // ---- Needs attention ------------------------------------------------------------------------------------------

    /// <summary>
    /// What needs a look, most pressing first; empty when all is well. A report that did not load adds nothing.
    /// </summary>
    public static IEnumerable<TodayCard> AttentionItems(Inputs inputs, DateOnly today, TimeZoneInfo timeZone)
    {
        foreach (var budget in (inputs.Budgets?.Items ?? new List<Budget>()).Where(IsOver))
        {
            yield return Attention($"budget-over.{budget.Id}", "budget-over", "Budget over its limit",
                "{budget} is over its limit ({used} used)",
                new Dictionary<string, TodayValue>
                {
                    ["budget"] = TodayValue.Plain(budget.Name),
                    ["used"] = TodayValue.Percent(Math.Round(budget.PercentageUsed, MidpointRounding.AwayFromZero))
                },
                TodaySeverity.Bad, BudgetsLink);
        }

        var accounts = inputs.Accounts ?? Array.Empty<Account>();
        if (LastImportDay(accounts, timeZone) is { } last)
        {
            var days = today.DayNumber - last.DayNumber;
            if (days > ImportReminderDays)
            {
                yield return Attention("statements-not-imported", "statements-not-imported", "No recent import",
                    "No statements imported in {days} days",
                    new Dictionary<string, TodayValue> { ["days"] = TodayValue.Count(days) },
                    TodaySeverity.Warning, TransactionsImportLink);
            }
        }

        foreach (var account in AccountsBehind(accounts, today))
        {
            yield return Attention($"statements-behind.{account.Id}", "statements-behind", "Statement to import",
                "{account}: statements reach {reach}. Import last month's",
                new Dictionary<string, TodayValue>
                {
                    ["account"] = TodayValue.Plain(account.Name),
                    ["reach"] = TodayValue.Day(StatementsReach(account)!.Value)
                },
                TodaySeverity.Warning, TransactionsImportLink);
        }

        foreach (var payment in inputs.Recurring?.Payments ?? new List<RecurringPayment>())
        {
            if (payment.PriceChange is not { } change || payment.Status == "Stopped" ||
                today.DayNumber - Day(change.Since).DayNumber > PriceChangeDays)
            {
                continue;
            }
            yield return Attention($"price-changed.{payment.Name}", "price-changed", "Price change",
                change.To > change.From ? "{payment} went up from {from} to {to}" : "{payment} went down from {from} to {to}",
                new Dictionary<string, TodayValue>
                {
                    ["payment"] = TodayValue.Plain(payment.Name),
                    ["from"] = TodayValue.Money(change.From),
                    ["to"] = TodayValue.Money(change.To)
                },
                TodaySeverity.Warning, SubscriptionsLink);
        }

        if (inputs.Uncategorised is { TransactionCount: > 0 and var unsorted })
        {
            yield return Attention("unsorted", "unsorted", "Unsorted transactions",
                unsorted == 1
                    ? "{count} transaction is in Other because no rule names its merchant"
                    : "{count} transactions are in Other because no rule names their merchants",
                new Dictionary<string, TodayValue> { ["count"] = TodayValue.Count(unsorted) },
                TodaySeverity.Warning, UnsortedLink);
        }
    }

    /// <summary>
    /// The day an account's imported statements reach: its latest transaction or its latest balance (Wealthsimple
    /// statements record one for their last day, even in a month without activity). Null when it has neither.
    /// </summary>
    public static DateOnly? StatementsReach(Account account)
    {
        DateOnly?[] days = [account.LastTransactionDate is { } t ? Day(t) : null, account.Balance is { } b ? Day(b.Date) : null];
        return days.Max();
    }

    /// <summary>
    /// Accounts whose statements stop before last month began: last month's statement is out and not imported yet.
    /// Accounts added by hand, and ones that nothing was ever imported into, are left alone.
    /// </summary>
    public static IEnumerable<Account> AccountsBehind(IEnumerable<Account> accounts, DateOnly today)
    {
        var lastMonthStart = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        return accounts.Where(account => !IsManual(account) && StatementsReach(account) < lastMonthStart);
    }

    /// <summary>
    /// The local day that statements were last imported: the latest import into an account that takes statements or,
    /// for accounts imported into before imports were recorded, the latest transaction's day. Null when never.
    /// </summary>
    public static DateOnly? LastImportDay(IEnumerable<Account> accounts, TimeZoneInfo timeZone)
    {
        var importable = accounts.Where(account => !IsManual(account)).ToList();
        var imported = importable
            .Where(account => account.LastImportedAt is not null)
            .Select(account => LocalDay(account.LastImportedAt!.Value, timeZone))
            .ToList();
        if (imported.Count > 0)
        {
            return imported.Max();
        }
        // Transaction dates are calendar days at midnight UTC: the same day here.
        var transactions = importable
            .Where(account => account.LastTransactionDate is not null)
            .Select(account => Day(account.LastTransactionDate!.Value))
            .ToList();
        return transactions.Count > 0 ? transactions.Max() : null;
    }

    private static bool IsManual(Account account) => string.Equals(account.Format, "Manual", StringComparison.OrdinalIgnoreCase);

    // ---- Helpers --------------------------------------------------------------------------------------------------

    /// <summary>"Oct 2026": how the finance reports label the month of a day.</summary>
    public static string MonthLabel(DateOnly day) => day.ToString("MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>A finance calendar day (midnight UTC) as a date.</summary>
    public static DateOnly Day(DateTime value) => DateOnly.FromDateTime(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value);

    /// <summary>A finance moment (UTC) as a day in <paramref name="timeZone"/>.</summary>
    public static DateOnly LocalDay(DateTime value, TimeZoneInfo timeZone)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone));
    }

    /// <summary>The text under a tile's value, with its look.</summary>
    private sealed record Hint(
        string Text,
        TodaySeverity Severity,
        IReadOnlyDictionary<string, TodayValue>? Values = null,
        TodayTrend? Trend = null);

    private static readonly IReadOnlyDictionary<string, TodayValue> NoValues = new Dictionary<string, TodayValue>();

    private static TodayCard Tile(string id, string kind, string title, TodayValue? value, Hint? hint, string? link) =>
        new($"money.{id}", Module, TodayCardType.Tile, kind, title, value, hint?.Text, hint?.Values ?? NoValues,
            hint?.Severity ?? TodaySeverity.Neutral, hint?.Trend, link);

    private static TodayCard Unavailable(string id, string kind, string title, string? link) =>
        new($"money.{id}", Module, TodayCardType.Tile, kind, title, null, NotLoaded, NoValues, TodaySeverity.Neutral, null, link);

    private static TodayCard Attention(string id, string kind, string title, string text,
        IReadOnlyDictionary<string, TodayValue> values, TodaySeverity severity, string link) =>
        new($"money.{id}", Module, TodayCardType.Attention, kind, title, null, text, values, severity, null, link);
}
