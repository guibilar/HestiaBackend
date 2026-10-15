using Microsoft.Extensions.Options;

namespace Hestia.Modules.Home.Application;

/// <summary>The settings of the Home module (section <c>Home</c>).</summary>
public sealed class HomeOptions
{
    public const string SectionName = "Home";

    /// <summary>
    /// IANA time zone (for example America/Edmonton) of this module. Without a value, the shared Hestia:TimeZone (T85,
    /// Docker: TZ) applies; set it here only to override that.
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>
    /// A warranty "ends soon" when its last day is today or in the next days (30): the alerts, the summary, and the
    /// filter <c>warranty=expiring</c>.
    /// </summary>
    public int WarrantySoonDays { get; set; } = 30;

    public TimeZoneInfo GetTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>True when the settings can work. The host checks this at start.</summary>
    public bool IsValid()
    {
        if (WarrantySoonDays is < 0 or > 366)
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
        "Home: set Home:TimeZone to an IANA time zone (for example America/Edmonton), and Home:WarrantySoonDays from 0 to 366.";
}

/// <summary>Which day it is for the Home module: the host's clock (<see cref="TimeProvider"/>) in <c>Home:TimeZone</c>.</summary>
public sealed class HomeDays(TimeProvider time, IOptions<HomeOptions> options)
{
    public DateTimeOffset Now => time.GetUtcNow();

    public DateOnly Today => DayOf(Now);

    /// <summary>The last day of "ends soon", seen on <paramref name="today"/>.</summary>
    public DateOnly SoonUntil(DateOnly today) => today.AddDays(options.Value.WarrantySoonDays);

    public DateOnly DayOf(DateTimeOffset moment) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, options.Value.GetTimeZone()).DateTime);
}
