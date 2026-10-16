using Hestia.Modules.Calendar.Contracts;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Modules.Tasks.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.Tasks.Application;

/// <summary>The Calendar items of the module (T103): each open task on its due day (a repeated task: its next due day).</summary>
public sealed class TaskCalendar(TasksDbContext db) : ICalendarSource
{
    public string Module => "Tasks";

    public async Task<CalendarItems> ListAsync(CalendarRange range, CancellationToken cancellationToken)
    {
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.Status == TaskItemStatus.Open && t.DueOn >= range.From && t.DueOn <= range.To)
            .Select(t => new { t.Id, t.Title, t.DueOn, Repeats = t.RepeatEvery != null })
            .ToListAsync(cancellationToken);
        return new CalendarItems(tasks.Select(t => CalendarItem.Day($"tasks.task-due.{t.Id:D}", Module, "task-due", t.Title,
            t.Repeats ? "Repeats" : null, t.DueOn!.Value, $"/tasks/{t.Id:D}", done: false)).ToList());
    }
}
