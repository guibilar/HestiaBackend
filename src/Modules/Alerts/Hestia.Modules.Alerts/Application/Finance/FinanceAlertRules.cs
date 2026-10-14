using Hestia.Modules.Alerts.Contracts;

namespace Hestia.Modules.Alerts.Application.Finance;

/// <summary>
/// The finance (Money) "Needs attention" rules, from the finance reports (T15: moved from the Today module, with the same
/// results for the same data). The rules are the same as the web dashboard (<c>attention.ts</c>) and the Android app
/// (<c>core/logic/Attention.kt</c>). A report that did not load is null: its rules give nothing, and their keys stay as
/// they are (the result does not cover them).
/// </summary>
public static class FinanceAlertRules
{
    public const string Module = "Money";

    /// <summary>More days than this without a statement import is worth a mention.</summary>
    public const int ImportReminderDays = 7;

    /// <summary>Price changes this recent are worth a mention.</summary>
    public const int PriceChangeDays = 45;

    public const string BudgetsLink = "/money/budgets";
    /// <summary>The Transactions page with the statement import open (T57).</summary>
    public const string TransactionsImportLink = "/money/transactions#import";
    public const string SubscriptionsLink = "/money/subscriptions";
    public const string UnsortedLink = "/money/rules#unsorted";

    // The key prefixes of each rule: what a check covers when the report of the rule loaded.
    public const string BudgetOverPrefix = "money.budget-over.";
    public const string NotImportedKey = "money.statements-not-imported";
    public const string BehindPrefix = "money.statements-behind.";
    public const string PriceChangedPrefix = "money.price-changed.";
    public const string UnsortedKey = "money.unsorted";

    /// <summary>The sort key of a budget alert that an event raised: after the budgets that a check found, until the next check.</summary>
    public const string EventBudgetSortKey = "1|9999";

    /// <summary>Everything that the rules read. Null: that report did not load.</summary>
    public sealed record Inputs(
        BudgetPage? Budgets,
        UncategorisedReport? Uncategorised,
        RecurringReport? Recurring,
        IReadOnlyList<Account>? Accounts);

    /// <summary>The alerts, and the keys that the loaded reports cover.</summary>
    public static AlertCheckResult Check(Inputs inputs, DateOnly today, TimeZoneInfo timeZone)
    {
        var covered = new List<string>();
        if (inputs.Budgets is not null)
        {
            covered.Add(BudgetOverPrefix);
        }
        if (inputs.Accounts is not null)
        {
            covered.Add(NotImportedKey);
            covered.Add(BehindPrefix);
        }
        if (inputs.Recurring is not null)
        {
            covered.Add(PriceChangedPrefix);
        }
        if (inputs.Uncategorised is not null)
        {
            covered.Add(UnsortedKey);
        }
        var complete = inputs.Budgets is not null && inputs.Accounts is not null && inputs.Recurring is not null &&
                       inputs.Uncategorised is not null;
        return new AlertCheckResult(Alerts(inputs, today, timeZone).ToList(), covered, complete);
    }

    /// <summary>
    /// What needs a look, most pressing first (the sort keys keep this order); empty when all is well. A report that did
    /// not load adds nothing.
    /// </summary>
    public static IEnumerable<AlertCandidate> Alerts(Inputs inputs, DateOnly today, TimeZoneInfo timeZone)
    {
        var index = 0;
        foreach (var budget in (inputs.Budgets?.Items ?? new List<Budget>()).Where(IsOver))
        {
            yield return BudgetOver(budget.Id, budget.Name, budget.PercentageUsed, $"1|{index++:D4}");
        }

        var accounts = inputs.Accounts ?? Array.Empty<Account>();
        if (LastImportDay(accounts, timeZone) is { } last)
        {
            var days = today.DayNumber - last.DayNumber;
            if (days > ImportReminderDays)
            {
                yield return Alert(NotImportedKey, "statements-not-imported", "No recent import",
                    "No statements imported in {days} days",
                    new Dictionary<string, AlertValue> { ["days"] = AlertValue.Count(days) },
                    AlertSeverity.Warning, TransactionsImportLink, "2");
            }
        }

        index = 0;
        foreach (var account in AccountsBehind(accounts, today))
        {
            yield return Alert($"{BehindPrefix}{account.Id}", "statements-behind", "Statement to import",
                "{account}: statements reach {reach}. Import last month's",
                new Dictionary<string, AlertValue>
                {
                    ["account"] = AlertValue.Plain(account.Name),
                    ["reach"] = AlertValue.Day(StatementsReach(account)!.Value)
                },
                AlertSeverity.Warning, TransactionsImportLink, $"3|{index++:D4}");
        }

        index = 0;
        foreach (var payment in inputs.Recurring?.Payments ?? new List<RecurringPayment>())
        {
            if (payment.PriceChange is not { } change || payment.Status == "Stopped" ||
                today.DayNumber - Day(change.Since).DayNumber > PriceChangeDays)
            {
                continue;
            }
            yield return Alert($"{PriceChangedPrefix}{payment.Name}", "price-changed", "Price change",
                change.To > change.From ? "{payment} went up from {from} to {to}" : "{payment} went down from {from} to {to}",
                new Dictionary<string, AlertValue>
                {
                    ["payment"] = AlertValue.Plain(payment.Name),
                    ["from"] = AlertValue.Money(change.From),
                    ["to"] = AlertValue.Money(change.To)
                },
                AlertSeverity.Warning, SubscriptionsLink, $"4|{index++:D4}");
        }

        if (inputs.Uncategorised is { TransactionCount: > 0 and var unsorted })
        {
            yield return Alert(UnsortedKey, "unsorted", "Unsorted transactions",
                unsorted == 1
                    ? "{count} transaction is in Other because no rule names its merchant"
                    : "{count} transactions are in Other because no rule names their merchants",
                new Dictionary<string, AlertValue> { ["count"] = AlertValue.Count(unsorted) },
                AlertSeverity.Warning, UnsortedLink, "5");
        }
    }

    /// <summary>A budget at or over its limit. The checks and the <c>BudgetLimitReached</c> event make the same alert.</summary>
    public static AlertCandidate BudgetOver(Guid budgetId, string name, decimal percentageUsed, string sortKey) =>
        Alert($"{BudgetOverPrefix}{budgetId}", "budget-over", "Budget over its limit",
            "{budget} is over its limit ({used} used)",
            new Dictionary<string, AlertValue>
            {
                ["budget"] = AlertValue.Plain(name),
                ["used"] = AlertValue.Percent(Math.Round(percentageUsed, MidpointRounding.AwayFromZero))
            },
            AlertSeverity.Bad, BudgetsLink, sortKey);

    public static bool IsOver(Budget budget) => budget.PercentageUsed >= 100;

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

    private static AlertCandidate Alert(string key, string kind, string title, string text,
        IReadOnlyDictionary<string, AlertValue> values, AlertSeverity severity, string link, string sortKey) =>
        new(key, kind, title, text, values, severity, link, sortKey);
}
