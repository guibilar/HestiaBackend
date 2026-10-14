using System.Net.Http.Json;
using System.Text.Json;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Modules.Tasks.Infrastructure;
using Hestia.Platform.Events;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Tasks.Tests;

/// <summary>
/// The Tasks events are durable from the first migration (T38): each event is a row of <c>tasks.EventOutbox</c>, stored
/// with the change, and the host's sender delivers it, also after a restart.
/// </summary>
public class DurableEventTests
{
    [Fact]
    public async Task EachEvent_ReachesItsConsumers_AndLeavesTheOutbox()
    {
        await using var host = TasksHost.Start();

        var task = await host.CreateAsync("Smoke detector batteries", TasksHost.Today, TasksHost.Every(12, RepeatUnit.Months));
        await host.DoneAsync(task.Id);
        await host.UndoAsync(task.Id);
        await host.Client.DeleteAsync($"{TasksHost.Tasks}/{task.Id}");

        await host.Factory.Events.WaitForAsync<TaskCreated>(e => e.TaskId == task.Id);
        var completed = await host.Factory.Events.WaitForAsync<TaskCompleted>(e => e.TaskId == task.Id);
        Assert.Equal(((DateOnly?)TasksHost.Today, (DateOnly?)TasksHost.Today.AddYears(1)), (completed.DueOn, completed.NextDueOn));
        var reopened = await host.Factory.Events.WaitForAsync<TaskReopened>(e => e.TaskId == task.Id);
        Assert.Equal(completed.CompletionId, reopened.CompletionId);
        await host.Factory.Events.WaitForAsync<TaskDeleted>(e => e.TaskId == task.Id);
        await Wait.UntilAsync(() => OutboxAsync(host.Factory), rows => rows.Count == 0);
        Assert.Empty(await OutboxAsync(host.Factory));
    }

    [Fact]
    public async Task TaskCreated_IsStoredInTheOutbox_WithTheTask_AndSurvivesARestart()
    {
        var clock = new TestClock(TasksHost.Now);
        // The first host commits the task and its event, and stops before it sends the event.
        var first = new HestiaApiFactory(TasksHost.Settings(new Dictionary<string, string?> { ["Events:Outbox:Enabled"] = "false" }),
            TasksHost.Services(clock));
        var response = await first.CreateClient().PostAsJsonAsync(TasksHost.Tasks, new CreateTaskRequest("Water the plants"), HestiaApiFactory.Json);
        var task = await response.ReadAsync<TaskDto>();

        var row = Assert.Single(await OutboxAsync(first));
        Assert.Equal(typeof(TaskCreated).FullName, row.EventType);
        var stored = JsonSerializer.Deserialize<TaskCreated>(row.Payload)!;
        Assert.Equal(task.Id, stored.TaskId);
        Assert.Empty(await first.Events.AfterQuietPeriodAsync<TaskCreated>());

        // The next host, on the same database, sends it with the same event id.
        await using var second = first.Restarted(TasksHost.Settings(), TasksHost.Services(clock));
        await first.DisposeAsync();
        second.CreateClient();

        var received = await second.Events.WaitForAsync<TaskCreated>(e => e.TaskId == task.Id);
        Assert.Equal(stored.EventId, received.EventId);
        await Wait.UntilAsync(() => OutboxAsync(second), rows => rows.Count == 0);
    }

    private static Task<List<OutboxMessage>> OutboxAsync(HestiaApiFactory factory) =>
        factory.WithScopeAsync(services =>
            services.GetRequiredService<TasksDbContext>().Set<OutboxMessage>().AsNoTracking().OrderBy(row => row.Id).ToListAsync());
}
