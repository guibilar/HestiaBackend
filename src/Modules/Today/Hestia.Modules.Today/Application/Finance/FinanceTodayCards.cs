using Hestia.Modules.Today.Contracts;
using Hestia.Platform.Finance;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.Today.Application.Finance;

/// <summary>
/// The finance (Money) tiles of Today. Finance is a separate service (D6), so its provider lives in the Today module.
/// It reads the finance reports through <see cref="IFinanceClient"/> as the signed-in user (D13), all at the same time,
/// and never reads the finance database. A report that does not load leaves its tile without a value, and the other
/// tiles still show. The finance "Needs attention" items come from the Alerts module (T15).
/// </summary>
public sealed class FinanceTodayCards(IFinanceClient finance, ILogger<FinanceTodayCards> logger) : ITodayCardProvider
{
    public string Module => FinanceCards.Module;

    /// <summary>First on Today (D11).</summary>
    public int Order => 0;

    public async Task<TodayModuleCards> GetCardsAsync(TodayContext context, CancellationToken cancellationToken)
    {
        // The same requests as the web dashboard.
        var month = Load(() => finance.GetAsync<CategorizedReport>("/api/Reports/current-month-categorized-graph", cancellationToken));
        var year = Load(() => finance.GetAsync<CategorizedReport>("/api/Reports/monthly-categorized-graph", cancellationToken));
        var cashFlow = Load(() => finance.GetAsync<CashFlowReport>("/api/Reports/cash-flow", cancellationToken));
        var netWorth = Load(() => finance.GetAsync<NetWorthReport>("/api/Reports/net-worth", cancellationToken));
        // A finance read-only query: a read-only user can call it too.
        var budgets = Load(() => finance.PostAsync<BudgetFilter, BudgetPage>("/api/Budget/Filter", BudgetFilter.AllActive, cancellationToken));

        await Task.WhenAll(month, year, cashFlow, netWorth, budgets);

        var inputs = new FinanceCards.Inputs(month.Result, year.Result, cashFlow.Result, netWorth.Result, budgets.Result);
        object?[] all = [inputs.Month, inputs.Year, inputs.CashFlow, inputs.NetWorth, inputs.Budgets];
        if (all.All(part => part is null))
        {
            // Finance is down: the whole module failed, not four empty tiles.
            throw new FinanceRequestException("Finance gave none of the reports for the Today tiles.");
        }
        return new TodayModuleCards(FinanceCards.Make(inputs, context.Date), Complete: all.All(part => part is not null));
    }

    /// <summary>One finance report, or null when finance did not give it (the error is in the log).</summary>
    private async Task<T?> Load<T>(Func<Task<T>> request) where T : class
    {
        try
        {
            return await request();
        }
        catch (FinanceRequestException exception)
        {
            logger.LogWarning(exception, "Today: a finance report did not load ({Type})", typeof(T).Name);
            return null;
        }
    }
}
