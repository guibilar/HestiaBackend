using Microsoft.Extensions.Options;

namespace Hestia.Modules.Tasks.Application;

/// <summary>The settings of the Tasks module (section <c>Tasks</c>).</summary>
public sealed class TasksOptions
{
    public const string SectionName = "Tasks";

    /// <summary>
    /// IANA time zone (for example America/Edmonton) that decides which day it is: "today", "overdue", and the day a task
    /// was done. Use the same zone as Today and Alerts. Docker sets it from <c>TZ</c>.
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>The days after today that "upcoming" covers (the list filter and the summary).</summary>
    public int UpcomingDays { get; set; } = 7;

    /// <summary>
    /// The most overdue tasks with an alert each. With more overdue tasks, one alert (<c>tasks.overdue-many</c>) takes
    /// their place, so the "Needs attention" list stays short.
    /// </summary>
    public int MaxOverdueAlerts { get; set; } = 5;

    public TimeZoneInfo GetTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>True when the settings can work. The host checks this at start.</summary>
    public bool IsValid()
    {
        if (UpcomingDays is < 1 or > 366 || MaxOverdueAlerts < 1)
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
        "Tasks: set Tasks:TimeZone to an IANA time zone (for example America/Edmonton), Tasks:UpcomingDays from 1 to 366, " +
        "and Tasks:MaxOverdueAlerts to 1 or more.";
}

/// <summary>Which day it is for the Tasks module: the host's clock (<see cref="TimeProvider"/>) in <c>Tasks:TimeZone</c>.</summary>
public sealed class TaskDays(TimeProvider time, IOptions<TasksOptions> options)
{
    public DateTimeOffset Now => time.GetUtcNow();

    public DateOnly Today => DayOf(Now);

    public DateOnly DayOf(DateTimeOffset moment) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, options.Value.GetTimeZone()).DateTime);
}
