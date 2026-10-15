using Microsoft.Extensions.Options;

namespace Hestia.Modules.People.Application;

/// <summary>The settings of the People module (section <c>People</c>).</summary>
public sealed class PeopleOptions
{
    public const string SectionName = "People";

    /// <summary>
    /// IANA time zone (for example America/Edmonton) of this module. Without a value, the shared Hestia:TimeZone (T85,
    /// Docker: TZ) applies; set it here only to override that.
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>A birthday today or in the next days (7) is an alert ("Needs attention").</summary>
    public int BirthdayAlertDays { get; set; } = 7;

    /// <summary>The summary lists the birthdays today and in the next days (30).</summary>
    public int UpcomingDays { get; set; } = 30;

    public TimeZoneInfo GetTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>True when the settings can work. The host checks this at start.</summary>
    public bool IsValid()
    {
        if (BirthdayAlertDays is < 0 or > 366 || UpcomingDays is < 0 or > 366)
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
        "People: set People:TimeZone to an IANA time zone (for example America/Edmonton), and People:BirthdayAlertDays and " +
        "People:UpcomingDays from 0 to 366.";
}

/// <summary>Which day it is for the People module: the host's clock (<see cref="TimeProvider"/>) in <c>People:TimeZone</c>.</summary>
public sealed class PeopleDays(TimeProvider time, IOptions<PeopleOptions> options)
{
    public DateTimeOffset Now => time.GetUtcNow();

    public DateOnly Today => DayOf(Now);

    public PeopleOptions Options => options.Value;

    public DateOnly DayOf(DateTimeOffset moment) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, options.Value.GetTimeZone()).DateTime);
}
