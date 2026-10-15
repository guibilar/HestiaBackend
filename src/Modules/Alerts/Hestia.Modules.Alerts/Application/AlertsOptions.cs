namespace Hestia.Modules.Alerts.Application;

/// <summary>The settings of the Alerts module (section <c>Alerts</c>).</summary>
public sealed class AlertsOptions
{
    public const string SectionName = "Alerts";

    /// <summary>
    /// IANA time zone (for example America/Edmonton) of this module. Without a value, the shared Hestia:TimeZone (T85,
    /// Docker: TZ) applies; set it here only to override that.
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>
    /// How often the host checks all rules without a request. This covers rules with no event, for example a document
    /// that starts to expire because time passed, or "no statement imported for more than a week".
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a request for fresh alerts (Today, <c>GET /api/alerts</c>) waits for the check. Then it gets the stored alerts.</summary>
    public TimeSpan RefreshWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How many days a resolved alert stays in the table (for later use: history, notifications).</summary>
    public int KeepResolvedDays { get; set; } = 30;

    /// <summary>The longest wait that a timer can have.</summary>
    private static readonly TimeSpan MaxInterval = TimeSpan.FromDays(24);

    public TimeZoneInfo GetTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>True when the settings can work. The host checks this at start.</summary>
    public bool IsValid()
    {
        if (CheckInterval <= TimeSpan.Zero || CheckInterval > MaxInterval || RefreshWait <= TimeSpan.Zero || RefreshWait > MaxInterval ||
            KeepResolvedDays < 0)
        {
            return false;
        }
        try
        {
            GetTimeZone();
            return true;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    public const string ValidationMessage =
        "Alerts: set Alerts:TimeZone to an IANA time zone (for example America/Edmonton), Alerts:CheckInterval and " +
        "Alerts:RefreshWait to more than zero (and at most 24 days), and Alerts:KeepResolvedDays to zero or more.";
}
