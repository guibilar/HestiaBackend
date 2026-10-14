using Hestia.Modules.Tasks.Contracts;

namespace Hestia.Modules.Tasks.Domain;

/// <summary>The repeat rules: the next due date of a repeated task after a "done" (T19).</summary>
public static class TaskSchedule
{
    /// <summary>
    /// The next due date of a repeated task that was done on <paramref name="doneOn"/>.
    /// <list type="bullet">
    /// <item><see cref="RepeatMode.FromCompletion"/>: the day it was done, plus the repeat.</item>
    /// <item><see cref="RepeatMode.FromDueDate"/>: the due date plus the repeat. A task that was done late moves to the
    /// first date of the schedule after <paramref name="doneOn"/>, so it is not overdue again at once.</item>
    /// </list>
    /// </summary>
    /// <param name="dueOn">The due date that was done.</param>
    /// <param name="doneOn">The day it was done, in the time zone of the module.</param>
    /// <param name="repeat">The repeat.</param>
    /// <param name="day">The day of the month of the schedule (<see cref="TaskItem.RepeatDay"/>), or null for the day of <paramref name="dueOn"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The next date is after the year 9999.</exception>
    public static DateOnly Next(DateOnly dueOn, DateOnly doneOn, TaskRepeatDto repeat, int? day)
    {
        if (repeat.Mode == RepeatMode.FromCompletion)
        {
            return Add(doneOn, repeat.Every, repeat.Unit, doneOn.Day);
        }

        if (repeat.Unit is RepeatUnit.Days or RepeatUnit.Weeks)
        {
            // One step; a task done late jumps over the missed dates at once.
            var step = repeat.Unit == RepeatUnit.Weeks ? 7 * repeat.Every : repeat.Every;
            var steps = Math.Max(1, (doneOn.DayNumber - dueOn.DayNumber) / step + 1);
            return dueOn.AddDays(steps * step);
        }
        // Months and years: at most 12 * 10000 steps, each on the day of the schedule (no drift after a short month).
        var next = Add(dueOn, repeat.Every, repeat.Unit, day ?? dueOn.Day);
        while (next <= doneOn)
        {
            next = Add(next, repeat.Every, repeat.Unit, day ?? dueOn.Day);
        }
        return next;
    }

    /// <summary>
    /// <paramref name="from"/> plus <paramref name="every"/> units. For months and years, the result is on
    /// <paramref name="day"/> of its month, or on the last day of a shorter month (Jan 31 + 1 month = Feb 28, or Feb 29
    /// in a leap year).
    /// </summary>
    public static DateOnly Add(DateOnly from, int every, RepeatUnit unit, int day) => unit switch
    {
        RepeatUnit.Days => from.AddDays(every),
        RepeatUnit.Weeks => from.AddDays(7 * every),
        RepeatUnit.Months => OnDay(from.AddMonths(every), day),
        RepeatUnit.Years => OnDay(from.AddYears(every), day),
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Not a repeat unit.")
    };

    private static DateOnly OnDay(DateOnly date, int day) =>
        new(date.Year, date.Month, Math.Clamp(day, 1, DateTime.DaysInMonth(date.Year, date.Month)));
}
