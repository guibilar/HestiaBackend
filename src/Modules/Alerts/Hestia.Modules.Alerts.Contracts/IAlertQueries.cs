namespace Hestia.Modules.Alerts.Contracts;

/// <summary>
/// How other modules read the open alerts, for example Today for its "Needs attention" list. The Alerts module registers
/// the implementation (a singleton).
/// </summary>
public interface IAlertQueries
{
    /// <summary>
    /// The open alerts, and the status of each module that has alert rules. With <see cref="AlertListOptions.Refresh"/>,
    /// or when a source has no complete check since the host started, it first asks for a new check and waits for it
    /// (at most <c>Alerts:RefreshWait</c>). After that time, it answers with the stored alerts.
    /// </summary>
    Task<AlertList> GetOpenAsync(AlertListOptions options, CancellationToken cancellationToken = default);
}

/// <param name="Refresh">True: check all sources first (for example pull to refresh on Today).</param>
/// <param name="Order">The order of the alerts in the answer.</param>
public sealed record AlertListOptions(bool Refresh = false, AlertOrder Order = AlertOrder.Severity);

public enum AlertOrder
{
    /// <summary>Most severe first, then newest first. For <c>GET /api/alerts</c>.</summary>
    Severity,

    /// <summary>In module order (Money first), then in the order of each module's rules. For Today.</summary>
    Module
}

/// <summary>The answer of <c>GET /api/alerts</c>.</summary>
/// <param name="CheckedAt">When the last check of all sources finished, or null when none did since the host started.</param>
/// <param name="Modules">Each module that has alert rules, with its status, in module order.</param>
/// <param name="Alerts">The open alerts.</param>
public sealed record AlertList(DateTimeOffset? CheckedAt, IReadOnlyList<AlertModuleDto> Modules, IReadOnlyList<AlertDto> Alerts);

/// <param name="Module">The module name, for example <c>Money</c>.</param>
/// <param name="Order">Its place on Today: lower first.</param>
/// <param name="Status">Whether the last check of its rules was complete.</param>
public sealed record AlertModuleDto(string Module, int Order, AlertModuleStatus Status);

public enum AlertModuleStatus
{
    /// <summary>The last check of all its rules was complete.</summary>
    Complete,

    /// <summary>Some data did not load in the last check, or the last check failed. The stored alerts are shown.</summary>
    Partial,

    /// <summary>No check worked since the host started, and the module has no stored alerts.</summary>
    Failed
}

/// <summary>
/// One open alert, in the format of a Today "Needs attention" card (the card fields <c>value</c> and <c>trend</c> are
/// always null for an alert, so they are left out). The text has placeholders; the clients fill them from
/// <see cref="Values"/> and format the amounts and dates.
/// </summary>
/// <param name="Id">The stable key, for example <c>money.budget-over.&lt;budget id&gt;</c>: the Today card id.</param>
/// <param name="AlertId">This alert. When a condition ends and comes back, the new alert has a new id.</param>
/// <param name="Module">The module name, for example <c>Money</c>.</param>
/// <param name="Kind">What the alert is about, inside its module, for example <c>budget-over</c>.</param>
/// <param name="Title">A short name of its kind.</param>
/// <param name="Text">The sentence, with placeholders.</param>
/// <param name="Values">The value of each placeholder.</param>
/// <param name="Severity">How the alert looks.</param>
/// <param name="Link">A client route, or null.</param>
/// <param name="RaisedAt">When the condition started (when a check first found it).</param>
/// <param name="UpdatedAt">When a check last changed the text, a value, or the severity.</param>
public sealed record AlertDto(
    string Id,
    Guid AlertId,
    string Module,
    string Kind,
    string Title,
    string Text,
    IReadOnlyDictionary<string, AlertValue> Values,
    AlertSeverity Severity,
    string? Link,
    DateTimeOffset RaisedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Always <c>Attention</c>: an alert is an item of the "Needs attention" list.</summary>
    public string Type => "Attention";
}
