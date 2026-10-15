namespace Hestia.Modules.Timeline.Application;

/// <summary>The settings of the Timeline module (section <c>Timeline</c>).</summary>
public sealed class TimelineOptions
{
    public const string SectionName = "Timeline";

    /// <summary>The most days that one page can have.</summary>
    public const int MaxDays = 31;

    /// <summary>
    /// IANA time zone (for example America/Edmonton) of this module. Without a value, the shared Hestia:TimeZone (T85,
    /// Docker: TZ) applies; set it here only to override that.
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>How long the Timeline waits for one module's source.</summary>
    public TimeSpan SourceTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeZoneInfo GetTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    public bool IsValid()
    {
        if (SourceTimeout <= TimeSpan.Zero)
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
        "Timeline: set Timeline:TimeZone to an IANA time zone (for example America/Edmonton), and Timeline:SourceTimeout to more than zero.";
}
