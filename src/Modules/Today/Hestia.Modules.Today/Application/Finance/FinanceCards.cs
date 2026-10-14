using System.Globalization;
using Hestia.Modules.Today.Contracts;

namespace Hestia.Modules.Today.Application.Finance;

/// <summary>
/// The four finance tiles of Today, from the finance reports. The rules are the same as the web dashboard
/// (<c>FinanceTilesComponent</c>, <c>dashboard-stats.ts</c>). A report that did not load is null.
/// The finance "Needs attention" items are alerts now (T15): the Alerts module has their rules, and Today reads them
/// through <c>IAlertQueries</c>.
/// </summary>
public static class FinanceCards
{
    public const string Module = "Money";

    /// <summary>The text of a tile whose report did not load.</summary>
    public const string NotLoaded = "Could not load this";

    public const string BudgetsLink = "/money/budgets";
    public const string NetWorthLink = "/money/net-worth";

    /// <summary>Everything that the finance tiles read. Null: that report did not load.</summary>
    public sealed record Inputs(
        CategorizedReport? Month,
        CategorizedReport? Year,
        CashFlowReport? CashFlow,
        NetWorthReport? NetWorth,
        BudgetPage? Budgets);

    /// <summary>The four tiles.</summary>
    public static IReadOnlyList<TodayCard> Make(Inputs inputs, DateOnly today) =>
    [
        SpentTile(inputs.Month, inputs.Year, today),
        LeftOverTile(inputs.CashFlow, today),
        NetWorthTile(inputs.NetWorth),
        BudgetsTile(inputs.Budgets)
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

    // ---- Helpers --------------------------------------------------------------------------------------------------

    /// <summary>"Oct 2026": how the finance reports label the month of a day.</summary>
    public static string MonthLabel(DateOnly day) => day.ToString("MMM yyyy", CultureInfo.InvariantCulture);

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
}
