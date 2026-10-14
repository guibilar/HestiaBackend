using Hestia.Modules.Alerts.Contracts;
using Hestia.Platform.Finance;
using Microsoft.Extensions.Logging;

namespace Hestia.Modules.Alerts.Application.Finance;

/// <summary>
/// The finance (Money) alert rules. Finance is a separate service (D6), so its source lives in the Alerts module. It
/// reads four finance reports at the same time, as the system identity with read-only access (D13, T42), and never
/// reads the finance database. It always checks everything: it ignores <see cref="AlertCheckContext.Target"/>.
/// </summary>
public sealed class FinanceAlertSource(IFinanceClient finance, ILogger<FinanceAlertSource> logger) : IAlertSource
{
    public const string SourceName = "Money.Finance";

    /// <summary>The caller name in the system token: <c>system:Alerts.FinanceAlertSource</c>.</summary>
    public const string Caller = "Alerts.FinanceAlertSource";

    public static readonly AlertSourceInfo Info = new(SourceName, FinanceAlertRules.Module, 0);

    public string Name => SourceName;

    public string Module => FinanceAlertRules.Module;

    /// <summary>First on Today (D11).</summary>
    public int Order => 0;

    public async Task<AlertCheckResult> CheckAsync(AlertCheckContext context, CancellationToken cancellationToken)
    {
        var client = finance.AsSystem(Caller);
        // The same requests as the web dashboard. The budget filter is a finance read-only query.
        var budgets = Load(() => client.PostAsync<BudgetFilter, BudgetPage>("/api/Budget/Filter", BudgetFilter.AllActive, cancellationToken));
        var uncategorised = Load(() => client.GetAsync<UncategorisedReport>("/api/Reports/uncategorised", cancellationToken));
        var recurring = Load(() => client.GetAsync<RecurringReport>("/api/Reports/recurring", cancellationToken));
        var accounts = Load(() => client.GetAsync<List<Account>>("/api/Accounts", cancellationToken));

        await Task.WhenAll(budgets, uncategorised, recurring, accounts);

        var inputs = new FinanceAlertRules.Inputs(budgets.Result, uncategorised.Result, recurring.Result, accounts.Result);
        if (inputs is { Budgets: null, Uncategorised: null, Recurring: null, Accounts: null })
        {
            // Finance is down: the check failed, and all finance alerts stay as they are.
            throw new FinanceRequestException("Finance gave none of the reports for the alerts.");
        }
        return FinanceAlertRules.Check(inputs, context.Date, context.TimeZone);
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
            logger.LogWarning(exception, "Alerts: a finance report did not load ({Type})", typeof(T).Name);
            return null;
        }
    }
}
