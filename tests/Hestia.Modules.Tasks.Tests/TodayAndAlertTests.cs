using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Alerts.Infrastructure;
using Hestia.Modules.Tasks.Application;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Modules.Today.Contracts;
using Hestia.Platform.Events;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Hestia.Modules.Tasks.Tests;

/// <summary>
/// The Tasks tile on Today, and the overdue alerts (T19): one alert per overdue task, or one alert for many. The Alerts
/// module stores them; Today shows them as "Needs attention" items.
/// </summary>
public class TodayAndAlertTests
{
    private const string Tasks = TasksHost.Tasks;
    private static readonly DateOnly Today = TasksHost.Today;

    [Fact]
    public async Task TheTile_CountsTheTasksDueToday_WithTheOverdueCount()
    {
        await using var host = TasksHost.Start();
        await host.CreateAsync("Today 1", Today);
        await host.CreateAsync("Today 2", Today);
        var doneToday = await host.CreateAsync("Today, done", Today);
        await host.DoneAsync(doneToday.Id);
        await host.CreateAsync("Late", Today.AddDays(-2));
        await host.CreateAsync("Tomorrow", Today.AddDays(1));

        var today = await (await host.Client.GetAsync("/api/today")).ReadAsync<TodayDto>();

        Assert.Equal(new TodayModuleDto("Tasks", TodayModuleStatus.Complete), Assert.Single(today.Modules, m => m.Module == "Tasks"));
        var tile = Assert.Single(today.Cards, card => card.Module == "Tasks" && card.Type == TodayCardType.Tile);
        Assert.Equal(("tasks.due-today", "due-today", "Tasks", TodayValue.Count(2), "{overdue} overdue", TodaySeverity.Warning, "/tasks"),
            (tile.Id, tile.Kind, tile.Title, tile.Value, tile.Text, tile.Severity, tile.Link));
        Assert.Equal(TodayValue.Count(1), tile.Values["overdue"]);
    }

    [Fact]
    public async Task TheTile_WithoutOverdueTasks_IsNeutral_WithoutAText()
    {
        await using var host = TasksHost.Start();

        var today = await (await host.Client.GetAsync("/api/today")).ReadAsync<TodayDto>();

        var tile = Assert.Single(today.Cards, card => card.Module == "Tasks" && card.Type == TodayCardType.Tile);
        Assert.Equal((TodayValue.Count(0), (string?)null, TodaySeverity.Neutral), (tile.Value, tile.Text, tile.Severity));
        Assert.Empty(tile.Values);
    }

    [Fact]
    public async Task OneOverdueTask_IsAnAlert_AndAnAttentionItemOnToday()
    {
        await using var host = TasksHost.Start();
        var late = await host.CreateAsync("Pay the plumber", Today.AddDays(-3));
        await host.CreateAsync("Due today", Today);
        await host.CreateAsync("Some day");

        var key = $"tasks.overdue.{late.Id}";
        var list = await Wait.UntilAsync(() => AlertsAsync(host), alerts => alerts.Alerts.Any(a => a.Id == key));

        var alert = Assert.Single(list.Alerts, a => a.Module == "Tasks");
        Assert.Equal((key, "overdue", "Task overdue", "{task} is {days} days late", AlertSeverity.Warning, $"/tasks/{late.Id}"),
            (alert.Id, alert.Kind, alert.Title, alert.Text, alert.Severity, alert.Link));
        Assert.Equal(AlertValue.Plain("Pay the plumber"), alert.Values["task"]);
        Assert.Equal(AlertValue.Day(Today.AddDays(-3)), alert.Values["date"]);
        Assert.Equal(AlertValue.Count(3), alert.Values["days"]);
        Assert.Equal(AlertModuleStatus.Complete, Assert.Single(list.Modules, m => m.Module == "Tasks").Status);

        var today = await (await host.Client.GetAsync("/api/today?refresh=true")).ReadAsync<TodayDto>();
        var item = Assert.Single(today.Cards, card => card.Module == "Tasks" && card.Type == TodayCardType.Attention);
        Assert.Equal((key, "{task} is {days} days late", TodaySeverity.Warning), (item.Id, item.Text, item.Severity));
    }

    [Fact]
    public async Task TheAlert_Resolves_OnDone_OnANewDueDate_AndOnDelete()
    {
        await using var host = TasksHost.Start();
        await AlertsAsync(host);
        var done = await host.CreateAsync("Done", Today.AddDays(-1));
        var moved = await host.CreateAsync("Moved", Today.AddDays(-1));
        var deleted = await host.CreateAsync("Deleted", Today.AddDays(-1));
        var keys = new[] { done.Id, moved.Id, deleted.Id }.Select(id => $"tasks.overdue.{id}").ToList();
        var raised = await Wait.UntilAsync(() => AlertsAsync(host), list => keys.All(key => list.Alerts.Any(a => a.Id == key)));
        Assert.Equal("{task} was due yesterday", raised.Alerts.First(a => a.Id == keys[0]).Text);

        await host.DoneAsync(done.Id);
        await host.Client.PutAsJsonAsync($"{Tasks}/{moved.Id}", new UpdateTaskRequest("Moved", DueOn: Today.AddDays(1)), HestiaApiFactory.Json);
        await host.Client.DeleteAsync($"{Tasks}/{deleted.Id}");

        var after = await Wait.UntilAsync(() => AlertsAsync(host), list => list.Alerts.All(a => a.Module != "Tasks"));
        Assert.DoesNotContain(after.Alerts, a => a.Module == "Tasks");

        // An undo brings the alert back.
        await host.UndoAsync(done.Id);
        await Wait.UntilAsync(() => AlertsAsync(host), list => list.Alerts.Any(a => a.Id == keys[0]));
    }

    [Fact]
    public async Task ManyOverdueTasks_AreOneAlert_AndFewerAreOneAlertEach_Again()
    {
        await using var host = TasksHost.Start(new Dictionary<string, string?> { ["Tasks:MaxOverdueAlerts"] = "2" });
        var tasks = new List<TaskDto>();
        for (var i = 1; i <= 3; i++)
        {
            tasks.Add(await host.CreateAsync($"Late {i}", Today.AddDays(-i)));
        }

        var many = await Wait.UntilAsync(() => AlertsAsync(host), list => list.Alerts.Any(a => a.Id == TaskDueAlerts.OverdueManyKey));
        var alert = Assert.Single(many.Alerts, a => a.Module == "Tasks");
        Assert.Equal((TaskDueAlerts.OverdueManyKey, "overdue-many", "{count} tasks are overdue", "/tasks?due=overdue"),
            (alert.Id, alert.Kind, alert.Text, alert.Link));
        Assert.Equal(AlertValue.Count(3), alert.Values["count"]);

        // Two left: one alert each again, and the "many" alert resolves.
        await host.DoneAsync(tasks[0].Id);
        var few = await Wait.UntilAsync(() => AlertsAsync(host), list => list.Alerts.All(a => a.Id != TaskDueAlerts.OverdueManyKey));
        Assert.Equal(new[] { $"tasks.overdue.{tasks[2].Id}", $"tasks.overdue.{tasks[1].Id}" }.Order(),
            few.Alerts.Where(a => a.Module == "Tasks").Select(a => a.Id).Order());
    }

    [Fact]
    public async Task ATaskThatBecomesOverdue_AsTimePasses_IsAnAlert_AfterTheNextCheck()
    {
        await using var host = TasksHost.Start();
        var task = await host.CreateAsync("Due today", Today);
        await Wait.UntilAsync(() => Task.FromResult(host.Factory.Events.Events.OfType<TaskCreated>().Any()), found => found);
        Assert.DoesNotContain((await AlertsAsync(host, refresh: true)).Alerts, a => a.Module == "Tasks");

        host.Clock.Now = TasksHost.Now.AddDays(1);

        var list = await AlertsAsync(host, refresh: true);
        var alert = Assert.Single(list.Alerts, a => a.Module == "Tasks");
        Assert.Equal(($"tasks.overdue.{task.Id}", "{task} was due yesterday"), (alert.Id, alert.Text));
    }

    [Fact]
    public async Task TheSameTaskEvent_Twice_GivesOneAlert()
    {
        await using var host = TasksHost.Start();
        var late = await host.CreateAsync("Late", Today.AddDays(-2));
        var key = $"tasks.overdue.{late.Id}";
        var first = Assert.Single((await Wait.UntilAsync(() => AlertsAsync(host), list => list.Alerts.Any(a => a.Id == key))).Alerts,
            a => a.Id == key);
        var again = new TaskCreated(late.Id, "Late", Today.AddDays(-2), [], null, TasksHost.Now);

        await DeliverAsync(host, again);
        await DeliverAsync(host, again);

        Assert.Equal(first.AlertId, Assert.Single((await AlertsAsync(host)).Alerts, a => a.Id == key).AlertId);
        Assert.Equal(1, await host.Factory.WithScopeAsync(services => services.GetRequiredService<AlertsDbContext>().Set<InboxEntry>()
            .CountAsync(entry => entry.EventId == again.EventId && entry.Consumer == TaskAlertsConsumer.InboxName)));
    }

    [Fact]
    public void TheRules_GiveTheKey_TheText_AndTheSortOrder()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000001");

        var yesterday = TaskDueAlerts.Overdue(id, "Pay the rent", Today.AddDays(-1), Today);
        var week = TaskDueAlerts.Overdue(id, "Pay the rent", Today.AddDays(-7), Today);

        Assert.Equal(("tasks.overdue.00000000-0000-0000-0000-000000000001", "{task} was due yesterday", AlertSeverity.Warning),
            (yesterday.Key, yesterday.Text, yesterday.Severity));
        Assert.Equal(("{task} is {days} days late", AlertValue.Count(7)), (week.Text, week.Values["days"]));
        // The oldest due date first.
        Assert.True(string.CompareOrdinal(week.SortKey, yesterday.SortKey) < 0);
        Assert.Equal(AlertValue.Count(9), TaskDueAlerts.Many(9).Values["count"]);
    }

    private static async Task<AlertList> AlertsAsync(TasksHost host, bool refresh = false)
    {
        var response = await host.Client.GetAsync(refresh ? "/api/alerts?refresh=true" : "/api/alerts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<AlertList>();
    }

    private static async Task DeliverAsync(TasksHost host, object message)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message);
    }
}
