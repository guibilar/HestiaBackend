using Hestia.Contracts.Finance;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Platform.Events;

namespace Hestia.Modules.Alerts.Application.Finance;

/// <summary>
/// Keeps the finance alerts current when finance changes (D7). Each finance event asks for a check of the finance
/// source in the background (several events before the check starts give one check). <see cref="BudgetLimitReached"/>
/// at the limit also raises its alert at once, from the event, so the alert is there even when the check cannot reach
/// finance.
/// </summary>
/// <remarks>
/// Only the direct raise changes data, so only it uses the inbox of the Alerts module (T36). A request for a check needs
/// no inbox: a second check gives the same result.
/// </remarks>
public sealed class FinanceAlertsConsumer(AlertsRunner runner)
{
    /// <summary>The inbox name of this consumer: "&lt;Module&gt;.&lt;Class&gt;". Do not change it later.</summary>
    public const string InboxName = "Alerts.FinanceAlertsConsumer";

    private const string OnlyAsksForACheck = "It only asks for a check; a second check gives the same result.";

    public async Task Consume(BudgetLimitReached message, CancellationToken cancellationToken)
    {
        if (message.Percent >= BudgetLimitReached.LimitPercent)
        {
            // The same "used" value as finance's PercentageUsed: the spending against the limit.
            var used = message.Limit > 0 ? message.Spent / message.Limit * 100 : message.Percent;
            var alert = FinanceAlertRules.BudgetOver(message.BudgetId, message.BudgetName, used, FinanceAlertRules.EventBudgetSortKey);
            await runner.RaiseAsync(FinanceAlertSource.Info, alert, new AlertTrigger(message.EventId, InboxName), cancellationToken);
        }
        _ = runner.RequestAsync(FinanceAlertSource.SourceName);
    }

    [ChangesNoData(OnlyAsksForACheck)]
    public void Consume(TransactionsImported message) => _ = runner.RequestAsync(FinanceAlertSource.SourceName);

    [ChangesNoData(OnlyAsksForACheck)]
    public void Consume(TransactionChanged message) => _ = runner.RequestAsync(FinanceAlertSource.SourceName);

    [ChangesNoData(OnlyAsksForACheck)]
    public void Consume(BalanceRecorded message) => _ = runner.RequestAsync(FinanceAlertSource.SourceName);

    [ChangesNoData(OnlyAsksForACheck)]
    public void Consume(SubscriptionPriceChanged message) => _ = runner.RequestAsync(FinanceAlertSource.SourceName);
}
