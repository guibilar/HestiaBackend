using Microsoft.Extensions.Options;

namespace Hestia.Modules.Calendar.Application;

/// <summary>The settings of the Calendar module (section <c>Calendar</c>).</summary>
public sealed class CalendarOptions
{
    public const string SectionName = "Calendar";

    /// <summary>
    /// IANA time zone (for example America/Edmonton) of this module. Without a value, the shared Hestia:TimeZone (T85,
    /// Docker: TZ) applies; set it here only to override that.
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>How long the Calendar waits for the source of one module. After it, that module is "Failed".</summary>
    public TimeSpan SourceTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeZoneInfo GetTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>True when the settings can work. The host checks this at start.</summary>
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
        "Calendar: set Calendar:TimeZone to an IANA time zone (for example America/Edmonton), and SourceTimeout above zero.";
}

/// <summary>Which day it is for the Calendar module: the host's clock (<see cref="TimeProvider"/>) in its time zone.</summary>
public sealed class CalendarDays(TimeProvider time, IOptions<CalendarOptions> options)
{
    public DateTimeOffset Now => time.GetUtcNow();

    public TimeZoneInfo TimeZone => options.Value.GetTimeZone();

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, TimeZone).DateTime);
}
