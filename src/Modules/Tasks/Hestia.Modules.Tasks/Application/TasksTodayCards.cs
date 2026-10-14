using Hestia.Modules.Today.Contracts;

namespace Hestia.Modules.Tasks.Application;

/// <summary>
/// The Tasks tile on Today (T19): the count of open tasks due today, and the overdue count as a second value. The day is
/// the day of the Tasks module (<c>Tasks:TimeZone</c>), so the tile and <c>GET /api/tasks/summary</c> agree.
/// </summary>
public sealed class TasksTodayCards(TaskService tasks) : ITodayCardProvider
{
    public const string TileId = "tasks.due-today";

    public string Module => TaskDueAlerts.ModuleName;

    /// <summary>After Documents (100).</summary>
    public int Order => 110;

    public async Task<TodayModuleCards> GetCardsAsync(TodayContext context, CancellationToken cancellationToken)
    {
        var counts = await tasks.CountAsync(cancellationToken);
        return new TodayModuleCards([Tile(counts.DueToday, counts.Overdue)]);
    }

    public static TodayCard Tile(int dueToday, int overdue) => new(
        TileId,
        TaskDueAlerts.ModuleName,
        TodayCardType.Tile,
        "due-today",
        "Tasks",
        TodayValue.Count(dueToday),
        overdue > 0 ? "{overdue} overdue" : null,
        overdue > 0 ? new Dictionary<string, TodayValue> { ["overdue"] = TodayValue.Count(overdue) } : new Dictionary<string, TodayValue>(),
        overdue > 0 ? TodaySeverity.Warning : TodaySeverity.Neutral,
        null,
        "/tasks");
}
