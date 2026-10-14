using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Today.Contracts;

namespace Hestia.Modules.Today.Application;

/// <summary>An alert (T15) as a Today "Needs attention" card: the same id, texts, values, and link.</summary>
public static class AlertCards
{
    public static TodayCard Card(AlertDto alert) =>
        new(alert.Id, alert.Module, TodayCardType.Attention, alert.Kind, alert.Title, null, alert.Text,
            alert.Values.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal),
            Severity(alert.Severity), null, alert.Link);

    public static TodayValue Value(AlertValue value) => value.Type switch
    {
        AlertValueType.Money => TodayValue.Money(value.Number ?? 0),
        AlertValueType.Count => new TodayValue(TodayValueType.Count, Number: value.Number),
        AlertValueType.Percent => TodayValue.Percent(value.Number ?? 0),
        AlertValueType.Date => new TodayValue(TodayValueType.Date, Date: value.Date),
        _ => TodayValue.Plain(value.Text ?? string.Empty)
    };

    public static TodaySeverity Severity(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Good => TodaySeverity.Good,
        AlertSeverity.Warning => TodaySeverity.Warning,
        AlertSeverity.Bad => TodaySeverity.Bad,
        _ => TodaySeverity.Neutral
    };

    public static TodayModuleStatus Status(AlertModuleStatus status) => status switch
    {
        AlertModuleStatus.Complete => TodayModuleStatus.Complete,
        AlertModuleStatus.Partial => TodayModuleStatus.Partial,
        _ => TodayModuleStatus.Failed
    };
}
