using Hestia.Modules.Alerts.Application.Finance;
using Hestia.Modules.Alerts.Contracts;

namespace Hestia.Modules.Alerts.Tests;

/// <summary>
/// The finance "Needs attention" rules, case by case (T15: moved from the Today finance card tests, with the same cases).
/// The cases come from the web dashboard tests (attention.spec.ts) and the Android AttentionTest, so the three places
/// give the same answer.
/// </summary>
public sealed class FinanceAlertRulesTests
{
    private static readonly DateOnly September5 = new(2026, 9, 5);
    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    private static Account NewAccount(string name, string format = "Cibc", string? last = null, string? balanceDate = null,
        string? importedAt = null) =>
        new(Guid.NewGuid(), name, format,
            last is null ? null : Utc(last),
            importedAt is null ? null : Utc(importedAt),
            balanceDate is null ? null : new AccountBalance(Utc(balanceDate), 100m));

    private static DateTime Utc(string value) => DateTime.Parse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

    private static FinanceAlertRules.Inputs Inputs(BudgetPage? budgets = null, UncategorisedReport? unsorted = null,
        RecurringReport? recurring = null, IReadOnlyList<Account>? accounts = null) =>
        new(budgets, unsorted, recurring, accounts);

    [Fact]
    public void AccountsBehind_StopBeforeLastMonth_ByTransactionsOrBalances()
    {
        var accounts = new[]
        {
            NewAccount("TFSA", "Wealthsimple", "2026-06-16T00:00:00Z", "2026-06-30T00:00:00Z"),
            // A quiet month still brings a balance for its last day.
            NewAccount("RRSP", "Wealthsimple", "2026-03-02T00:00:00Z", "2026-08-31T00:00:00Z"),
            NewAccount("Chequing", "Cibc", "2026-09-02T00:00:00Z"),
            NewAccount("Cash", "Manual", "2026-01-02T00:00:00Z"),
            NewAccount("Unused")
        };

        Assert.Equal(new[] { "TFSA" }, FinanceAlertRules.AccountsBehind(accounts, September5).Select(a => a.Name));
        Assert.Equal(new DateOnly(2026, 6, 30), FinanceAlertRules.StatementsReach(accounts[0]));
    }

    [Fact]
    public void Alerts_ListBudgetsOver_StatementsBehind_RecentPriceChanges_AndUnsorted()
    {
        RecurringPayment Payment(string name, string since, string status = "Active") =>
            new(name, status, new PriceChange(16.49m, 18.99m, Utc(since)));

        var items = FinanceAlertRules.Alerts(
            Inputs(
                budgets: new BudgetPage { Items = [new Budget(Guid.NewGuid(), "Groceries", 120m)] },
                unsorted: new UncategorisedReport(1),
                recurring: new RecurringReport
                {
                    Payments =
                    [
                        Payment("NETFLIX", "2026-08-20T00:00:00Z"),
                        Payment("OLD GYM", "2026-03-20T00:00:00Z"),
                        Payment("STOPPED", "2026-08-25T00:00:00Z", "Stopped")
                    ]
                },
                accounts: [NewAccount("TFSA", "Wealthsimple", balanceDate: "2026-06-30T00:00:00Z")]),
            September5, Toronto).ToList();

        Assert.Equal(
            new[]
            {
                ("budget-over", "{budget} is over its limit ({used} used)"),
                ("statements-behind", "{account}: statements reach {reach}. Import last month's"),
                ("price-changed", "{payment} went up from {from} to {to}"),
                ("unsorted", "{count} transaction is in Other because no rule names its merchant")
            },
            items.Select(item => (item.Kind, item.Text)));
        Assert.All(items, item => Assert.StartsWith("money.", item.Key));
        Assert.Equal(AlertValue.Percent(120m), items[0].Values["used"]);
        Assert.Equal(AlertSeverity.Bad, items[0].Severity);
        Assert.Equal(AlertValue.Day(new DateOnly(2026, 6, 30)), items[1].Values["reach"]);
        Assert.Equal(FinanceAlertRules.TransactionsImportLink, items[1].Link);
        // The sort keys keep the order of the rules on Today.
        Assert.Equal(items.Select(item => item.SortKey).Order(StringComparer.Ordinal), items.Select(item => item.SortKey));
    }

    [Fact]
    public void Alerts_SayWhenNothingWasImportedForMoreThanAWeek()
    {
        var accounts = new[] { NewAccount("Chequing", "Cibc", "2026-08-26T00:00:00Z", importedAt: "2026-08-27T15:00:00Z") };

        var items = FinanceAlertRules.Alerts(Inputs(accounts: accounts), September5, Toronto).ToList();

        var item = Assert.Single(items);
        Assert.Equal(("money.statements-not-imported", "No statements imported in {days} days"), (item.Key, item.Text));
        Assert.Equal(AlertValue.Count(9), item.Values["days"]);
        Assert.Equal("/money/transactions#import", item.Link);
        Assert.Empty(FinanceAlertRules.Alerts(Inputs(accounts: accounts), new DateOnly(2026, 9, 3), Toronto));
    }

    [Fact]
    public void LastImportDay_IsTheLocalDay_OfTheImport()
    {
        // 01:30 UTC on August 28th is still August 27th in Toronto.
        var accounts = new[] { NewAccount("Chequing", importedAt: "2026-08-28T01:30:00Z") };

        Assert.Equal(new DateOnly(2026, 8, 27), FinanceAlertRules.LastImportDay(accounts, Toronto));
    }

    [Fact]
    public void Alerts_ManyUnsorted_UseThePlural_AndNothingIsAnEmptyList()
    {
        var item = Assert.Single(FinanceAlertRules.Alerts(Inputs(unsorted: new UncategorisedReport(3)), September5, Toronto));
        Assert.Equal("{count} transactions are in Other because no rule names their merchants", item.Text);

        Assert.Empty(FinanceAlertRules.Alerts(Inputs(), September5, Toronto));
    }

    [Fact]
    public void Check_CoversOnlyTheRulesWhoseReportLoaded()
    {
        var all = FinanceAlertRules.Check(
            Inputs(new BudgetPage(), new UncategorisedReport(0), new RecurringReport(), []), September5, Toronto);
        Assert.True(all.Complete);
        Assert.Equal(
            new[] { "money.budget-over.", "money.statements-not-imported", "money.statements-behind.", "money.price-changed.", "money.unsorted" },
            all.Covered);

        var noAccounts = FinanceAlertRules.Check(
            Inputs(new BudgetPage(), new UncategorisedReport(0), new RecurringReport()), September5, Toronto);
        Assert.False(noAccounts.Complete);
        Assert.DoesNotContain(noAccounts.Covered, prefix => prefix.StartsWith("money.statements", StringComparison.Ordinal));
    }
}
