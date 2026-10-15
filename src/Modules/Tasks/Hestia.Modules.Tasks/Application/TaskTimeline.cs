using Hestia.Modules.Tasks.Infrastructure;
using Hestia.Modules.Timeline.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.Tasks.Application;

/// <summary>The Tasks entries of the Timeline (T16): each time a task was done (a repeated task once per completion).</summary>
public sealed class TaskTimeline(TasksDbContext db) : ITimelineSource
{
    public string Module => TaskDueAlerts.ModuleName;

    public async Task<TimelineEntries> ListAsync(TimelineRange range, CancellationToken cancellationToken)
    {
        var start = range.Start;
        var end = range.End;
        var done = await db.Completions.AsNoTracking()
            .Where(c => c.DoneAt >= start && c.DoneAt < end)
            .Join(db.Tasks.AsNoTracking(), c => c.TaskId, t => t.Id, (c, t) => new { c.Id, c.TaskId, c.DoneAt, t.Title, t.RepeatEvery })
            .ToListAsync(cancellationToken);
        return new TimelineEntries(done.Select(c => new TimelineEntry(
            $"tasks.task-done.{c.Id:D}", Module, "task-done", range.DayOf(c.DoneAt), c.DoneAt,
            c.Title, c.RepeatEvery is null ? "Done" : "Done · repeats", null, $"/tasks/{c.TaskId:D}")).ToList());
    }

    public async Task<DateOnly?> LatestBeforeAsync(TimelineRange range, CancellationToken cancellationToken)
    {
        var start = range.Start;
        // Only completions of tasks that still exist show (the list joins the tasks).
        var latest = await db.Completions.AsNoTracking().Where(c => c.DoneAt < start)
            .Join(db.Tasks.AsNoTracking(), c => c.TaskId, t => t.Id, (c, _) => c.DoneAt)
            .MaxAsync(at => (DateTimeOffset?)at, cancellationToken);
        return latest is { } at ? range.DayOf(at) : null;
    }
}
