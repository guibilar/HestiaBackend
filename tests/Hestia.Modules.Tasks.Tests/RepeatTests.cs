using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Modules.Tasks.Domain;
using Hestia.Testing;

namespace Hestia.Modules.Tasks.Tests;

/// <summary>"Done" and undo, and the repeat rules: each unit, both modes, the end of the month, and leap years (T19).</summary>
public class RepeatTests
{
    private const string Tasks = TasksHost.Tasks;
    private static readonly DateOnly Today = TasksHost.Today;

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static TaskRepeatDto Repeat(int every, RepeatUnit unit, RepeatMode mode = RepeatMode.FromDueDate) => new(every, unit, mode);

    // The rules alone.

    [Theory]
    [InlineData(1, RepeatUnit.Days, "2026-10-15")]
    [InlineData(4, RepeatUnit.Days, "2026-10-18")]
    [InlineData(1, RepeatUnit.Weeks, "2026-10-21")]
    [InlineData(2, RepeatUnit.Weeks, "2026-10-28")]
    [InlineData(1, RepeatUnit.Months, "2026-11-14")]
    [InlineData(12, RepeatUnit.Months, "2027-10-14")]
    [InlineData(1, RepeatUnit.Years, "2027-10-14")]
    [InlineData(5, RepeatUnit.Years, "2031-10-14")]
    public void EachUnit_AddsItsStep_ToTheDueDate(int every, RepeatUnit unit, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected, CultureInfo.InvariantCulture), TaskSchedule.Next(D(2026, 10, 14), D(2026, 10, 14), Repeat(every, unit), null));
    }

    [Theory]
    [InlineData(2027, "2027-02-28")]
    [InlineData(2028, "2028-02-29")]
    public void January31_PlusOneMonth_IsTheLastDayOfFebruary(int year, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected, CultureInfo.InvariantCulture), TaskSchedule.Next(D(year, 1, 31), D(year, 1, 31), Repeat(1, RepeatUnit.Months), 31));
    }

    [Fact]
    public void AMonthlySchedule_GoesBackToItsDay_AfterAShortMonth()
    {
        var repeat = Repeat(1, RepeatUnit.Months);
        var february = TaskSchedule.Next(D(2027, 1, 31), D(2027, 1, 31), repeat, 31);
        var march = TaskSchedule.Next(february, february, repeat, 31);
        var april = TaskSchedule.Next(march, march, repeat, 31);

        Assert.Equal(new[] { D(2027, 2, 28), D(2027, 3, 31), D(2027, 4, 30) }, new[] { february, march, april });
    }

    [Fact]
    public void February29_EveryYear_IsFebruary28_ThenFebruary29_InTheNextLeapYear()
    {
        var repeat = Repeat(1, RepeatUnit.Years);
        var dates = new List<DateOnly> { D(2028, 2, 29) };
        for (var i = 0; i < 4; i++)
        {
            dates.Add(TaskSchedule.Next(dates[^1], dates[^1], repeat, 29));
        }

        Assert.Equal(new[] { D(2028, 2, 29), D(2029, 2, 28), D(2030, 2, 28), D(2031, 2, 28), D(2032, 2, 29) }, dates);
    }

    [Fact]
    public void FromDueDate_DoneEarly_KeepsTheSchedule()
    {
        Assert.Equal(D(2026, 11, 1), TaskSchedule.Next(D(2026, 10, 1), D(2026, 9, 20), Repeat(1, RepeatUnit.Months), 1));
        Assert.Equal(D(2026, 10, 18), TaskSchedule.Next(D(2026, 10, 14), D(2026, 10, 5), Repeat(4, RepeatUnit.Days), null));
    }

    [Fact]
    public void FromDueDate_DoneLate_MovesToTheFirstDateOfTheScheduleAfterThatDay()
    {
        // Monthly on the 1st, done on Oct 14th: the next date is Nov 1st (not Sep 1st, which is past).
        Assert.Equal(D(2026, 11, 1), TaskSchedule.Next(D(2026, 8, 1), D(2026, 10, 14), Repeat(1, RepeatUnit.Months), 1));
        // Monthly on the 1st, done on Nov 1st: Dec 1st (a date on the day it was done is past).
        Assert.Equal(D(2026, 12, 1), TaskSchedule.Next(D(2026, 10, 1), D(2026, 11, 1), Repeat(1, RepeatUnit.Months), 1));
        // Every 2 weeks from Oct 1st, done on Oct 20th: Oct 29th.
        Assert.Equal(D(2026, 10, 29), TaskSchedule.Next(D(2026, 10, 1), D(2026, 10, 20), Repeat(2, RepeatUnit.Weeks), null));
        // Every 3 days from Oct 1st, done on Oct 7th: Oct 10th.
        Assert.Equal(D(2026, 10, 10), TaskSchedule.Next(D(2026, 10, 1), D(2026, 10, 7), Repeat(3, RepeatUnit.Days), null));
        // Every year, two years late.
        Assert.Equal(D(2027, 3, 1), TaskSchedule.Next(D(2024, 3, 1), D(2026, 10, 14), Repeat(1, RepeatUnit.Years), 1));
    }

    [Theory]
    [InlineData(4, RepeatUnit.Days, "2026-10-18")]
    [InlineData(1, RepeatUnit.Weeks, "2026-10-21")]
    [InlineData(1, RepeatUnit.Months, "2026-11-14")]
    [InlineData(1, RepeatUnit.Years, "2027-10-14")]
    public void FromCompletion_CountsFromTheDayItWasDone(int every, RepeatUnit unit, string expected)
    {
        // Due long ago, or not yet: only the day it was done counts.
        Assert.Equal(DateOnly.Parse(expected, CultureInfo.InvariantCulture), TaskSchedule.Next(D(2026, 9, 1), D(2026, 10, 14), Repeat(every, unit, RepeatMode.FromCompletion), 1));
        Assert.Equal(DateOnly.Parse(expected, CultureInfo.InvariantCulture), TaskSchedule.Next(D(2026, 12, 1), D(2026, 10, 14), Repeat(every, unit, RepeatMode.FromCompletion), 1));
    }

    [Fact]
    public void FromCompletion_OnJanuary31_PlusOneMonth_IsTheEndOfFebruary()
    {
        Assert.Equal(D(2028, 2, 29), TaskSchedule.Next(D(2028, 1, 15), D(2028, 1, 31), Repeat(1, RepeatUnit.Months, RepeatMode.FromCompletion), 15));
    }

    // Through the API.

    [Fact]
    public async Task Done_OnATaskWithoutARepeat_MakesItDone_WithACompletion()
    {
        await using var host = TasksHost.Start();
        var task = await host.CreateAsync("Book the chimney sweep", Today.AddDays(-1));

        var done = await host.DoneAsync(task.Id);

        Assert.Equal((TaskItemStatus.Done, (DateTimeOffset?)TasksHost.Now, (DateOnly?)Today.AddDays(-1), (DateTimeOffset?)TasksHost.Now),
            (done.Status, done.DoneAt, done.DueOn, done.LastDoneAt));
        var completion = Assert.Single(done.Completions);
        Assert.Equal((TasksHost.Now, (DateOnly?)Today.AddDays(-1)), (completion.DoneAt, completion.DueOn));
        var completed = await host.Factory.Events.WaitForAsync<TaskCompleted>();
        Assert.Equal((task.Id, completion.Id, (DateOnly?)Today.AddDays(-1), (DateOnly?)null),
            (completed.TaskId, completed.CompletionId, completed.DueOn, completed.NextDueOn));

        // Done twice is a conflict.
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsync($"{Tasks}/{task.Id}/done", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync($"{Tasks}/{Guid.NewGuid()}/done", null)).StatusCode);
    }

    [Fact]
    public async Task Done_OnARepeatedTask_MovesTheDueDate_AndKeepsItOpen()
    {
        await using var host = TasksHost.Start();
        var task = await host.CreateAsync("Smoke detector batteries", Today, TasksHost.Every(12, RepeatUnit.Months));

        var done = await host.DoneAsync(task.Id);

        Assert.Equal((TaskItemStatus.Open, (DateTimeOffset?)null, (DateOnly?)Today.AddMonths(12), (DateTimeOffset?)TasksHost.Now),
            (done.Status, done.DoneAt, done.DueOn, done.LastDoneAt));
        Assert.Equal((DateOnly?)Today, Assert.Single(done.Completions).DueOn);
        var completed = await host.Factory.Events.WaitForAsync<TaskCompleted>();
        Assert.Equal(((DateOnly?)Today, (DateOnly?)Today.AddMonths(12)), (completed.DueOn, completed.NextDueOn));
    }

    [Fact]
    public async Task ARepeatedTask_WithoutADueDate_IsDueToday()
    {
        await using var host = TasksHost.Start();

        var task = await host.CreateAsync("Water the plants", repeat: TasksHost.Every(4, RepeatUnit.Days, RepeatMode.FromCompletion));

        Assert.Equal(Today, task.DueOn);
        Assert.Equal(new TaskRepeatDto(4, RepeatUnit.Days, RepeatMode.FromCompletion), task.Repeat);
        // Without a mode, the schedule is from the due date.
        var fixedSchedule = await host.CreateAsync("Pay the rent", Today, new TaskRepeatRequest(1, RepeatUnit.Months, null));
        Assert.Equal(RepeatMode.FromDueDate, fixedSchedule.Repeat!.Mode);
    }

    [Fact]
    public async Task FromCompletion_ThroughTheApi_UsesTheDayInTheTimeZoneOfTheModule()
    {
        // 02:00 UTC on October 15th is October 14th in Toronto.
        await using var host = TasksHost.Start(now: new DateTimeOffset(2026, 10, 15, 2, 0, 0, TimeSpan.Zero));
        var task = await host.CreateAsync("Water the plants", Today.AddDays(-10), TasksHost.Every(4, RepeatUnit.Days, RepeatMode.FromCompletion));

        var done = await host.DoneAsync(task.Id);

        Assert.Equal(Today.AddDays(4), done.DueOn);
    }

    [Fact]
    public async Task AMonthlyTaskOnThe31st_KeepsItsDay_ThroughTheApi()
    {
        await using var host = TasksHost.Start(now: new DateTimeOffset(2027, 1, 20, 16, 0, 0, TimeSpan.Zero));
        var task = await host.CreateAsync("Pay the card", D(2027, 1, 31), TasksHost.Every(1, RepeatUnit.Months));

        var february = await host.DoneAsync(task.Id);
        // A change of the title only keeps the day of the schedule (31).
        await host.Client.PutAsJsonAsync($"{Tasks}/{task.Id}", new UpdateTaskRequest("Pay the credit card", null, february.DueOn, null,
            new TaskRepeatRequest(1, RepeatUnit.Months, RepeatMode.FromDueDate)), HestiaApiFactory.Json);
        var march = await host.DoneAsync(task.Id);

        Assert.Equal((D(2027, 2, 28), D(2027, 3, 31)), (february.DueOn, march.DueOn));
    }

    [Fact]
    public async Task Undo_OnADoneTask_OpensItAgain_AndRemovesTheCompletion()
    {
        await using var host = TasksHost.Start();
        var task = await host.CreateAsync("Book the chimney sweep", Today);
        await host.DoneAsync(task.Id);

        var undone = await host.UndoAsync(task.Id);

        Assert.Equal((TaskItemStatus.Open, (DateTimeOffset?)null, (DateOnly?)Today, (DateTimeOffset?)null),
            (undone.Status, undone.DoneAt, undone.DueOn, undone.LastDoneAt));
        Assert.Empty(undone.Completions);
        var reopened = await host.Factory.Events.WaitForAsync<TaskReopened>();
        Assert.Equal((task.Id, (DateOnly?)Today), (reopened.TaskId, reopened.DueOn));

        // Nothing more to undo.
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsync($"{Tasks}/{task.Id}/undo", null)).StatusCode);
    }

    [Fact]
    public async Task Undo_OnARepeatedTask_PutsBackTheDueDateOfTheLastCompletion_OneAtATime()
    {
        await using var host = TasksHost.Start();
        var task = await host.CreateAsync("Water the plants", Today, TasksHost.Every(4, RepeatUnit.Days));
        await host.DoneAsync(task.Id);
        host.Clock.Now = TasksHost.Now.AddMinutes(1);
        var twice = await host.DoneAsync(task.Id);
        Assert.Equal(Today.AddDays(8), twice.DueOn);
        Assert.Equal(new DateOnly?[] { Today.AddDays(4), Today }, twice.Completions.Select(c => c.DueOn));

        var first = await host.UndoAsync(task.Id);
        Assert.Equal((TaskItemStatus.Open, (DateOnly?)Today.AddDays(4), (DateTimeOffset?)TasksHost.Now),
            (first.Status, first.DueOn, first.LastDoneAt));
        Assert.Single(first.Completions);

        var second = await host.UndoAsync(task.Id);
        Assert.Equal((DateOnly?)Today, second.DueOn);
        Assert.Empty(second.Completions);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsync($"{Tasks}/{task.Id}/undo", null)).StatusCode);
    }

    [Fact]
    public async Task Get_ShowsTheLast10Completions_NewestFirst()
    {
        await using var host = TasksHost.Start();
        var task = await host.CreateAsync("Feed the sourdough starter", Today, TasksHost.Every(1, RepeatUnit.Days));
        for (var i = 0; i < 12; i++)
        {
            host.Clock.Now = TasksHost.Now.AddMinutes(i);
            await host.DoneAsync(task.Id);
        }

        var found = await host.GetAsync(task.Id);

        Assert.Equal(10, found.Completions.Count);
        Assert.Equal(TasksHost.Now.AddMinutes(11), found.Completions[0].DoneAt);
        Assert.Equal(TasksHost.Now.AddMinutes(11), found.LastDoneAt);
        Assert.Equal(found.Completions.Select(c => c.DoneAt).OrderDescending(), found.Completions.Select(c => c.DoneAt));
    }
}
