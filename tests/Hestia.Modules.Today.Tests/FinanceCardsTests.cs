using Hestia.Modules.Today.Application.Finance;
using Hestia.Modules.Today.Contracts;

namespace Hestia.Modules.Today.Tests;

/// <summary>
/// The finance tile rules, case by case. The cases come from the web dashboard tests (dashboard-stats.spec.ts), so the
/// places give the same answer. The attention rules are in the Alerts module now (T15): see FinanceAlertRulesTests.
/// </summary>
public sealed class FinanceCardsTests
{
    private static readonly DateOnly September5 = new(2026, 9, 5);

    private static CategorizedReport Month(decimal total, decimal? previousToDate = null) =>
        new() { MonthlyData = [new MonthTotal("Sep 2026", total)], PreviousMonthToDateTotal = previousToDate };

    private static CategorizedReport Year(decimal average, bool runningMonthOnly = false) =>
        new() { AverageMonthlyTotal = average, AverageIncludesCurrentMonth = runningMonthOnly };

    [Theory]
    [InlineData(2026, 9, 5, "Aug 1–5")]
    [InlineData(2026, 9, 1, "Aug 1")]
    // February is shorter: on March 30th it is all of it.
    [InlineData(2026, 3, 30, "Feb 1–28")]
    // Across the year boundary.
    [InlineData(2027, 1, 12, "Dec 1–12")]
    public void PreviousMonthToDateLabel_NamesThePartOfLastMonth(int year, int month, int day, string expected) =>
        Assert.Equal(expected, FinanceCards.PreviousMonthToDateLabel(new DateOnly(year, month, day)));

    [Fact]
    public void SpentTile_ReadsThisMonth_Or0WithoutData()
    {
        Assert.Equal(TodayValue.Money(100m), FinanceCards.SpentTile(Month(100m), null, September5).Value);
        Assert.Equal(TodayValue.Money(0m), FinanceCards.SpentTile(new CategorizedReport(), null, September5).Value);
    }

    [Fact]
    public void SpentTile_ComparesWithAUsualMonthByNow()
    {
        // 3000 a month: 500 by September 5th. 650 is 150 over.
        var tile = FinanceCards.SpentTile(Month(650m, previousToDate: 100m), Year(3000m), September5);

        Assert.Equal("{difference} over a usual month by now", tile.Text);
        Assert.Equal(TodayValue.Money(150m), tile.Values["difference"]);
        Assert.Equal((TodaySeverity.Bad, TodayTrend.Up), (tile.Severity, tile.Trend));
    }

    [Fact]
    public void SpentTile_InJanuary_ComparesWithLastMonthSoFar()
    {
        // No month of the year is over: the average is of the running month, so last month up to today says more.
        var tile = FinanceCards.SpentTile(Month(400m, previousToDate: 500m), Year(400m, runningMonthOnly: true), September5);

        Assert.Equal("{change} less than {period}", tile.Text);
        Assert.Equal(TodayValue.Percent(20m), tile.Values["change"]);
        Assert.Equal(TodayValue.Plain("Aug 1–5"), tile.Values["period"]);
        Assert.Equal((TodaySeverity.Good, TodayTrend.Down), (tile.Severity, tile.Trend));
    }

    [Fact]
    public void SpentTile_WithNothingToCompare_HasNoText()
    {
        var tile = FinanceCards.SpentTile(Month(400m, previousToDate: 0m), null, September5);

        Assert.Null(tile.Text);
        Assert.Equal((TodaySeverity.Neutral, (TodayTrend?)null), (tile.Severity, tile.Trend));
    }

    [Fact]
    public void Tiles_WithoutTheirReport_SayItDidNotLoad()
    {
        Assert.All(
            new[]
            {
                FinanceCards.SpentTile(null, Year(3000m), September5), FinanceCards.LeftOverTile(null, September5),
                FinanceCards.NetWorthTile(null), FinanceCards.BudgetsTile(null)
            },
            tile =>
            {
                Assert.Null(tile.Value);
                Assert.Equal("Could not load this", tile.Text);
            });
    }

    [Theory]
    [InlineData(0, 0, 0, false, "No income or spending yet", TodaySeverity.Neutral)]
    // Before payday, spending alone is not a warning.
    [InlineData(0, 300, -300, true, "No income yet this month", TodaySeverity.Neutral)]
    [InlineData(2000, 400, 1600, true, "Income less spending so far", TodaySeverity.Good)]
    [InlineData(1000, 1400, -400, true, "More went out than came in", TodaySeverity.Bad)]
    public void LeftOverTile_SaysHowTheMonthGoes(int income, int spending, int net, bool hasMonth, string text, TodaySeverity severity)
    {
        var report = new CashFlowReport
        {
            Months = hasMonth
                ? [new CashFlowMonth("Aug 2026", 1, 1, 0), new CashFlowMonth("Sep 2026", income, spending, net)]
                : [new CashFlowMonth("Aug 2026", 1, 1, 0)]
        };

        var tile = FinanceCards.LeftOverTile(report, September5);

        Assert.Equal(hasMonth ? TodayValue.Money(net) : null, tile.Value);
        Assert.Equal((text, severity), (tile.Text, tile.Severity));
    }

    [Fact]
    public void NetWorthTile_ShowsTheChangeSinceLastMonth_OnlyForTheSameAccounts()
    {
        var down = FinanceCards.NetWorthTile(new NetWorthReport
        {
            NetWorth = 9000m,
            Months = [new NetWorthMonth("Aug 2026", 10000m, 2), new NetWorthMonth("Sep 2026", 9000m, 2)]
        });
        Assert.Equal("−{change} since last month", down.Text);
        Assert.Equal(TodayValue.Money(1000m), down.Values["change"]);
        Assert.Equal((TodaySeverity.Bad, TodayTrend.Down), (down.Severity, down.Trend));

        // An account whose history starts later would read as a jump.
        var newAccount = FinanceCards.NetWorthTile(new NetWorthReport
        {
            NetWorth = 15000m,
            Months = [new NetWorthMonth("Aug 2026", 10000m, 1), new NetWorthMonth("Sep 2026", 15000m, 2)]
        });
        Assert.Null(newAccount.Text);

        var empty = FinanceCards.NetWorthTile(new NetWorthReport());
        Assert.Equal(("Enter a balance to start", TodaySeverity.Neutral), (empty.Text, empty.Severity));
    }

    [Fact]
    public void BudgetsTile_CountsBudgetsAtOrOverTheirLimit()
    {
        var tile = FinanceCards.BudgetsTile(new BudgetPage
        {
            Items = [new Budget(Guid.NewGuid(), "A", 100m), new Budget(Guid.NewGuid(), "B", 130m), new Budget(Guid.NewGuid(), "C", 99.9m)]
        });
        Assert.Equal(TodayValue.Plain("2 of 3 over"), tile.Value);

        var none = FinanceCards.BudgetsTile(new BudgetPage());
        Assert.Equal((TodayValue.Plain("None yet"), "Create one to track a limit"), (none.Value, none.Text));

        var fine = FinanceCards.BudgetsTile(new BudgetPage { Items = [new Budget(Guid.NewGuid(), "A", 10m)] });
        Assert.Equal(("All within limits", TodaySeverity.Good), (fine.Text, fine.Severity));
    }
}
