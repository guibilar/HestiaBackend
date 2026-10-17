using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hestia.Modules.Tasks.Application;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Modules.Tasks.Infrastructure;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Tasks.Tests;

/// <summary>Create, list, get, change, and delete tasks (T19).</summary>
public class TaskApiTests
{
    private const string Tasks = TasksHost.Tasks;
    private static readonly DateOnly Today = TasksHost.Today;

    [Fact]
    public async Task Create_StoresTheTask_AndRaisesTaskCreated()
    {
        await using var host = TasksHost.Start();

        var response = await host.Client.PostAsJsonAsync(Tasks, new CreateTaskRequest(
            "  Renew   car insurance ", "Call the broker", Today.AddDays(3), ["Car", "car, Money"], SortOrder: 2), HestiaApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = await response.ReadAsync<TaskDto>();
        Assert.Equal($"{Tasks}/{task.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(("Renew   car insurance", "Call the broker", (DateOnly?)Today.AddDays(3), TaskItemStatus.Open, (int?)2),
            (task.Title, task.Notes, task.DueOn, task.Status, task.SortOrder));
        Assert.Equal(new[] { "car", "money" }, task.Tags);
        Assert.Null(task.Repeat);
        Assert.Null(task.DoneAt);
        Assert.Null(task.LastDoneAt);
        Assert.Empty(task.Links);
        Assert.Empty(task.Completions);
        Assert.Equal(TasksHost.Now, task.CreatedAt);

        var created = await host.Factory.Events.WaitForAsync<TaskCreated>();
        Assert.Equal((task.Id, task.Title, task.DueOn, 1), (created.TaskId, created.Title, created.DueOn, created.Version));
        Assert.Equal(new[] { "car", "money" }, created.Tags);
    }

    [Fact]
    public async Task Create_WithOnlyATitle_HasNoDueDate()
    {
        await using var host = TasksHost.Start();

        var task = await host.CreateAsync("Read the boiler manual");

        Assert.Null(task.DueOn);
        Assert.Empty(task.Tags);
        Assert.Null(task.Notes);
    }

    [Fact]
    public async Task Create_WithBadValues_IsABadRequest_AndStoresNothing()
    {
        await using var host = TasksHost.Start();

        await TasksHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(Tasks, new CreateTaskRequest("  "), HestiaApiFactory.Json), "title");
        await TasksHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(Tasks, new CreateTaskRequest(new string('x', 201)), HestiaApiFactory.Json), "title");
        await TasksHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(Tasks,
            new CreateTaskRequest("Notes", new string('x', 4001)), HestiaApiFactory.Json), "notes");
        await TasksHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(Tasks,
            new CreateTaskRequest("Tag", Tags: ["a|b"]), HestiaApiFactory.Json), "tags");
        await TasksHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(Tasks,
            new CreateTaskRequest("Repeat", Repeat: new TaskRepeatRequest(0, RepeatUnit.Days, null)), HestiaApiFactory.Json), "repeat.every");
        await TasksHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(Tasks,
            new CreateTaskRequest("Repeat", Repeat: new TaskRepeatRequest(2, null, null)), HestiaApiFactory.Json), "repeat.unit");
        // An enum name that does not exist: the host cannot read the body.
        var unknownUnit = await host.Client.PostAsync(Tasks, JsonContent.Create(
            JsonDocument.Parse("""{"title":"Repeat","repeat":{"every":1,"unit":"Fortnights"}}""").RootElement));
        Assert.Equal(HttpStatusCode.BadRequest, unknownUnit.StatusCode);

        Assert.Equal(0, await host.WithDbAsync(db => db.Tasks.CountAsync()));
    }

    [Fact]
    public async Task Get_ReturnsTheTask_And404ForAnUnknownId()
    {
        await using var host = TasksHost.Start();
        var created = await host.CreateAsync("Descale the kettle", Today);

        var found = await host.GetAsync(created.Id);

        Assert.Equal((created.Id, "Descale the kettle"), (found.Id, found.Title));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"{Tasks}/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Update_ReplacesTheDetails_AndRaisesTaskChanged()
    {
        await using var host = TasksHost.Start();
        var created = await host.CreateAsync(new CreateTaskRequest("Old", "Old notes", Today, ["old"], SortOrder: 1));
        host.Clock.Now = TasksHost.Now.AddMinutes(5);

        var response = await host.Client.PutAsJsonAsync($"{Tasks}/{created.Id}",
            new UpdateTaskRequest("Clean the gutters", null, Today.AddDays(10), ["House"], TasksHost.Every(6, RepeatUnit.Months)),
            HestiaApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.ReadAsync<TaskDto>();
        Assert.Equal(("Clean the gutters", (string?)null, (DateOnly?)Today.AddDays(10), (int?)null),
            (updated.Title, updated.Notes, updated.DueOn, updated.SortOrder));
        Assert.Equal(new[] { "house" }, updated.Tags);
        Assert.Equal(new TaskRepeatDto(6, RepeatUnit.Months, RepeatMode.FromDueDate), updated.Repeat);
        Assert.Equal(TasksHost.Now.AddMinutes(5), updated.UpdatedAt);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);

        var changed = await host.Factory.Events.WaitForAsync<TaskChanged>();
        Assert.Equal((created.Id, TaskChange.Details, "Clean the gutters", (DateOnly?)Today.AddDays(10), TaskItemStatus.Open),
            (changed.TaskId, changed.Change, changed.Title, changed.DueOn, changed.Status));
        Assert.Equal(updated.Repeat, changed.Repeat);

        await TasksHost.AssertBadRequestAsync(await host.Client.PutAsJsonAsync($"{Tasks}/{created.Id}", new UpdateTaskRequest(""), HestiaApiFactory.Json), "title");
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PutAsJsonAsync($"{Tasks}/{Guid.NewGuid()}",
            new UpdateTaskRequest("Missing"), HestiaApiFactory.Json)).StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesTheTask_ItsCompletions_AndItsLinks_AndRaisesTaskDeleted()
    {
        await using var host = TasksHost.Start();
        var task = await host.CreateAsync("Water the plants", Today, TasksHost.Every(4, RepeatUnit.Days, RepeatMode.FromCompletion));
        await host.DoneAsync(task.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{Tasks}/{task.Id}")).StatusCode);

        var deleted = await host.Factory.Events.WaitForAsync<TaskDeleted>();
        Assert.Equal((task.Id, "Water the plants"), (deleted.TaskId, deleted.Title));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"{Tasks}/{task.Id}")).StatusCode);
        Assert.Equal(0, await host.WithDbAsync(db => db.Completions.CountAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"{Tasks}/{task.Id}")).StatusCode);
    }

    [Fact]
    public async Task List_IsInDueOrder_WithTheTasksWithoutADueDateLast()
    {
        await using var host = TasksHost.Start();
        var someDay = await host.CreateAsync("Some day");
        var later = await host.CreateAsync("Later", Today.AddDays(5));
        var todaySecond = await host.CreateAsync(new CreateTaskRequest("Today, second", DueOn: Today, SortOrder: 2));
        var todayFirst = await host.CreateAsync(new CreateTaskRequest("Today, first", DueOn: Today, SortOrder: 1));
        var todayNoOrder = await host.CreateAsync("Today, no order", Today);
        var late = await host.CreateAsync("Late", Today.AddDays(-2));

        var page = await host.ListAsync();

        Assert.Equal(new[] { late.Id, todayFirst.Id, todaySecond.Id, todayNoOrder.Id, later.Id, someDay.Id }, page.Items.Select(t => t.Id));
        Assert.Equal(6, page.TotalCount);

        var second = await host.ListAsync("page=2&pageSize=4");
        Assert.Equal(new[] { later.Id, someDay.Id }, second.Items.Select(t => t.Id));
        Assert.Equal((2, 4, 6), (second.Page, second.PageSize, second.TotalCount));
    }

    [Fact]
    public async Task DoneTasks_AreListed_TheLastDoneFirst()
    {
        await using var host = TasksHost.Start();
        var first = await host.CreateAsync("Done first", Today.AddDays(3));
        var second = await host.CreateAsync("Done second", Today.AddDays(-2));
        var third = await host.CreateAsync("Done third");
        await host.DoneAsync(first.Id);
        host.Clock.Now = host.Clock.Now.AddMinutes(5);
        await host.DoneAsync(second.Id);
        host.Clock.Now = host.Clock.Now.AddMinutes(5);
        await host.DoneAsync(third.Id);

        Assert.Equal(new[] { third.Id, second.Id, first.Id }, (await host.ListAsync("status=Done")).Items.Select(t => t.Id));
    }

    [Fact]
    public async Task Tags_ListsTheTagsInUse_WithTheirCounts()
    {
        await using var host = TasksHost.Start();
        Assert.Empty(await (await host.Client.GetAsync($"{Tasks}/tags")).ReadAsync<List<TagCountDto>>());
        await host.CreateAsync("Pay the plumber", tags: ["Money", "home"]);
        var done = await host.CreateAsync("Pay the rent", tags: ["money"]);
        await host.DoneAsync(done.Id);
        await host.CreateAsync("No tags");

        var tags = await (await host.Client.GetAsync($"{Tasks}/tags")).ReadAsync<List<TagCountDto>>();

        Assert.Equal(new[] { new TagCountDto("home", 1), new TagCountDto("money", 2) }, tags);
    }

    [Fact]
    public async Task List_FiltersByDue_Status_Tag_AndText()
    {
        await using var host = TasksHost.Start(new Dictionary<string, string?> { ["Tasks:UpcomingDays"] = "7" });
        var late = await host.CreateAsync("Pay the plumber", Today.AddDays(-1), tags: ["money"]);
        var lateDone = await host.CreateAsync("Late but done", Today.AddDays(-3));
        await host.DoneAsync(lateDone.Id);
        var today = await host.CreateAsync("Smoke detector batteries", Today, tags: ["safety"]);
        var inSeven = await host.CreateAsync("In seven days", Today.AddDays(7));
        var inEight = await host.CreateAsync("In eight days", Today.AddDays(8));
        var none = await host.CreateAsync(new CreateTaskRequest("Read the manual", "boiler model 2031"));

        Assert.Equal(new[] { today.Id }, (await host.ListAsync("due=today")).Items.Select(t => t.Id));
        Assert.Equal(new[] { late.Id }, (await host.ListAsync("due=overdue")).Items.Select(t => t.Id));
        Assert.Equal(new[] { inSeven.Id }, (await host.ListAsync("due=Upcoming")).Items.Select(t => t.Id));
        Assert.Equal(new[] { inEight.Id }, (await host.ListAsync("due=later")).Items.Select(t => t.Id));
        Assert.Equal(new[] { none.Id }, (await host.ListAsync("due=none")).Items.Select(t => t.Id));
        Assert.Equal(new[] { lateDone.Id }, (await host.ListAsync("status=done")).Items.Select(t => t.Id));
        Assert.Equal(5, (await host.ListAsync("status=Open")).TotalCount);
        Assert.Equal(new[] { late.Id }, (await host.ListAsync("tag=Money")).Items.Select(t => t.Id));
        Assert.Equal(new[] { today.Id }, (await host.ListAsync("q=SMOKE batteries")).Items.Select(t => t.Id));
        Assert.Equal(new[] { none.Id }, (await host.ListAsync("q=boiler")).Items.Select(t => t.Id));
        Assert.Equal(new[] { late.Id }, (await host.ListAsync("q=money")).Items.Select(t => t.Id));
        Assert.Empty((await host.ListAsync("due=today&tag=money")).Items);
        Assert.Contains(inEight.Id, (await host.ListAsync()).Items.Select(t => t.Id));

        await TasksHost.AssertBadRequestAsync(await host.Client.GetAsync($"{Tasks}?due=tomorrow"), "due");
        await TasksHost.AssertBadRequestAsync(await host.Client.GetAsync($"{Tasks}?status=Waiting"), "status");
        await TasksHost.AssertBadRequestAsync(await host.Client.GetAsync($"{Tasks}?status=1"), "status");
        await TasksHost.AssertBadRequestAsync(await host.Client.GetAsync($"{Tasks}?pageSize=101"), "pageSize");
    }

    [Fact]
    public async Task Today_IsTheDayInTheTimeZoneOfTheModule()
    {
        // 02:00 UTC on October 15th is still October 14th in Toronto (UTC-4).
        await using var host = TasksHost.Start(now: new DateTimeOffset(2026, 10, 15, 2, 0, 0, TimeSpan.Zero));
        var task = await host.CreateAsync("Today in Toronto", Today);

        Assert.Equal(new[] { task.Id }, (await host.ListAsync("due=today")).Items.Select(t => t.Id));
        var summary = await (await host.Client.GetAsync($"{Tasks}/summary")).ReadAsync<TaskCountsDto>();
        Assert.Equal((Today, 1, 0), (summary.Date, summary.DueToday, summary.Overdue));
    }

    [Fact]
    public async Task Summary_CountsTheOpenTasks_DueToday_Overdue_AndUpcoming()
    {
        await using var host = TasksHost.Start();
        await host.CreateAsync("Late 1", Today.AddDays(-1));
        await host.CreateAsync("Late 2", Today.AddDays(-30));
        var doneLate = await host.CreateAsync("Late, done", Today.AddDays(-2));
        await host.DoneAsync(doneLate.Id);
        await host.CreateAsync("Today", Today);
        await host.CreateAsync("Tomorrow", Today.AddDays(1));
        await host.CreateAsync("In a week", Today.AddDays(7));
        await host.CreateAsync("In two weeks", Today.AddDays(14));
        await host.CreateAsync("Some day");

        var summary = await (await host.Client.GetAsync($"{Tasks}/summary")).ReadAsync<TaskCountsDto>();

        Assert.Equal(new TaskCountsDto(Today, 1, 2, 2), summary);
    }

    [Fact]
    public async Task WithoutSignIn_EveryEndpointIs401()
    {
        await using var host = TasksHost.Start(signedIn: false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(Tasks)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync($"{Tasks}/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsJsonAsync(Tasks, new CreateTaskRequest("Task"))).StatusCode);
    }

    [Fact]
    public async Task AReadOnlyUser_CanRead_ButNotWrite()
    {
        await using var host = TasksHost.Start();
        var task = await host.CreateAsync("Water the plants", Today);
        var readOnly = host.Factory.CreateClient();
        readOnly.DefaultRequestHeaders.Add(TestSignIn.ReadOnlyHeader, "yes");

        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync(Tasks)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"{Tasks}/{task.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"{Tasks}/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"{Tasks}/by-link?kind=Documents.Document&id={Guid.NewGuid()}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync(Tasks, new CreateTaskRequest("New"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PutAsJsonAsync($"{Tasks}/{task.Id}", new UpdateTaskRequest("Changed"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.DeleteAsync($"{Tasks}/{task.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsync($"{Tasks}/{task.Id}/done", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsync($"{Tasks}/{task.Id}/undo", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync($"{Tasks}/{task.Id}/links",
            new CreateTaskLinkRequest(TaskLinkKinds.DocumentsDocument, Guid.NewGuid().ToString()))).StatusCode);

        Assert.Equal(TaskItemStatus.Open, (await host.GetAsync(task.Id)).Status);
    }

    [Fact]
    public async Task TheOpenApiDocument_DescribesTheTasksEndpoints()
    {
        await using var host = TasksHost.Start();

        var response = await host.Client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.Equal("CreateTask", paths.GetProperty(Tasks).GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("ListTasks", paths.GetProperty(Tasks).GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("CompleteTask", paths.GetProperty($"{Tasks}/{{id}}/done").GetProperty("post").GetProperty("operationId").GetString());
        Assert.True(paths.TryGetProperty($"{Tasks}/{{id}}/undo", out _));
        Assert.True(paths.TryGetProperty($"{Tasks}/summary", out _));
        Assert.True(paths.TryGetProperty($"{Tasks}/by-link", out _));
        Assert.True(paths.TryGetProperty($"{Tasks}/{{id}}/links/{{linkId}}", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("TaskDto", out _));
        Assert.True(schemas.TryGetProperty("TaskCountsDto", out _));
    }

    [Fact]
    public async Task Migrations_PutTheTablesAndTheirHistory_InTheTasksSchema()
    {
        Assert.SkipUnless(TestDatabase.OnPostgres, $"Needs PostgreSQL: set {TestDatabase.Variable}.");
        await using var host = TasksHost.Start();
        host.Factory.CreateClient();

        var tables = await host.Factory.WithScopeAsync(services =>
            services.GetRequiredService<TasksDbContext>().Database
                .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'tasks'")
                .ToListAsync());

        Assert.Equal(new[] { "EventInbox", "EventOutbox", "TaskCompletions", "TaskLinks", "Tasks", "__EFMigrationsHistory" },
            tables.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TwoDoneAtTheSameTime_OnARepeatedTask_CountOnce()
    {
        Assert.SkipUnless(TestDatabase.OnPostgres, $"Needs PostgreSQL: set {TestDatabase.Variable}.");
        await using var host = TasksHost.Start();
        var task = await host.CreateAsync("Water the plants", Today, TasksHost.Every(1, RepeatUnit.Weeks));
        await using var scope = host.Factory.Services.CreateAsyncScope();
        // The second "done" read the task before the first one saved: it added a second completion and moved the due date
        // from the old one.
        await scope.ServiceProvider.GetRequiredService<TasksDbContext>().Tasks.SingleAsync(t => t.Id == task.Id);
        await host.DoneAsync(task.Id);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            scope.ServiceProvider.GetRequiredService<TaskService>().DoneAsync(task.Id, CancellationToken.None));

        Assert.Equal(Today.AddDays(7), (await host.GetAsync(task.Id)).DueOn);
        Assert.Equal(1, await host.WithDbAsync(db => db.Completions.CountAsync(c => c.TaskId == task.Id)));
    }
}
