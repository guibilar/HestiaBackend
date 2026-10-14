using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Modules.Tasks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Tasks.Application;

/// <summary>
/// The Tasks alert rules (T19): a "Needs attention" item for each open task with a due date before today
/// (<c>tasks.overdue.&lt;id&gt;</c>). With more than <c>Tasks:MaxOverdueAlerts</c> (5) overdue tasks, one item
/// (<c>tasks.overdue-many</c>, "7 tasks are overdue") takes their place.
/// </summary>
/// <remarks>
/// The check always looks at all tasks: the count decides between the two forms. The Alerts module runs it on its timer
/// (a task becomes overdue because time passes), and <see cref="TaskAlertsConsumer"/> asks for it after each task event.
/// "Today" is the day of the check in <c>Tasks:TimeZone</c>.
/// </remarks>
public sealed class TaskDueAlerts(TasksDbContext db, TaskDays days, IOptions<TasksOptions> options) : IAlertSource
{
    public const string SourceName = "Tasks.Due";
    public const string ModuleName = "Tasks";
    public const string OverduePrefix = "tasks.overdue.";
    public const string OverdueManyKey = "tasks.overdue-many";

    public string Name => SourceName;

    public string Module => ModuleName;

    /// <summary>After Documents (100).</summary>
    public int Order => 110;

    public async Task<AlertCheckResult> CheckAsync(AlertCheckContext context, CancellationToken cancellationToken)
    {
        var today = days.DayOf(context.Now);
        var overdue = await db.Tasks.AsNoTracking()
            .Where(t => t.Status == TaskItemStatus.Open && t.DueOn < today)
            .Select(t => new { t.Id, t.Title, DueOn = t.DueOn!.Value })
            .ToListAsync(cancellationToken);
        IReadOnlyList<AlertCandidate> alerts = overdue.Count > options.Value.MaxOverdueAlerts
            ? [Many(overdue.Count)]
            : overdue.Select(t => Overdue(t.Id, t.Title, t.DueOn, today)).ToList();
        return new AlertCheckResult(alerts, [OverduePrefix, OverdueManyKey]);
    }

    /// <summary>The alert of one task that was due on <paramref name="dueOn"/>, seen on <paramref name="today"/>.</summary>
    public static AlertCandidate Overdue(Guid id, string title, DateOnly dueOn, DateOnly today)
    {
        var late = today.DayNumber - dueOn.DayNumber;
        var values = new Dictionary<string, AlertValue>
        {
            ["task"] = AlertValue.Plain(title),
            ["date"] = AlertValue.Day(dueOn),
            ["days"] = AlertValue.Count(late)
        };
        return new AlertCandidate($"{OverduePrefix}{id}", "overdue", "Task overdue",
            late == 1 ? "{task} was due yesterday" : "{task} is {days} days late", values, AlertSeverity.Warning,
            $"/tasks/{id}", $"{dueOn:yyyy-MM-dd}|{title}|{id}");
    }

    /// <summary>The one alert for many overdue tasks.</summary>
    public static AlertCandidate Many(int count) =>
        new(OverdueManyKey, "overdue-many", "Tasks overdue", "{count} tasks are overdue",
            new Dictionary<string, AlertValue> { ["count"] = AlertValue.Count(count) }, AlertSeverity.Warning,
            "/tasks?due=overdue", "0");
}
