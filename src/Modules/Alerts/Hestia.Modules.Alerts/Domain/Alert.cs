using Hestia.Modules.Alerts.Contracts;
using Hestia.Platform.Domain;

namespace Hestia.Modules.Alerts.Domain;

/// <summary>
/// One alert: a condition that needs attention, from the time a check found it (<see cref="RaisedAt"/>) to the time a
/// check found it gone (<see cref="ResolvedAt"/>). The same condition has one open alert (one <see cref="Key"/>).
/// </summary>
public class Alert : BaseEntity
{
    public const int MaxSourceLength = 100;
    public const int MaxModuleLength = 50;
    public const int MaxKeyLength = 200;
    public const int MaxKindLength = 50;
    public const int MaxTitleLength = 200;
    public const int MaxTextLength = 1000;
    public const int MaxLinkLength = 500;
    public const int MaxSortKeyLength = 300;

    /// <summary>The <see cref="IAlertSource.Name"/> that raised it, for example <c>Documents.Expiry</c>.</summary>
    public string Source { get; set; } = string.Empty;

    public string Module { get; set; } = string.Empty;

    /// <summary>Stable for the same condition: the Today card id, for example <c>documents.expiring.&lt;id&gt;</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;
    public AlertSeverity Severity { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>The sentence with placeholders.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The values of the placeholders, as JSON (<c>AlertValuesJson</c>).</summary>
    public string Values { get; set; } = "{}";

    public string? Link { get; set; }

    /// <summary>The place inside its module on Today (ordinal order).</summary>
    public string SortKey { get; set; } = string.Empty;

    public DateTimeOffset RaisedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Null while the condition holds.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
}
