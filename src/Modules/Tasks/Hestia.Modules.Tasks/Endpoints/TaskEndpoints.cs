using Hestia.Modules.Tasks.Application;
using Hestia.Modules.Tasks.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Hestia.Modules.Tasks.Endpoints;

/// <summary>The Tasks endpoints, under /api/tasks. A read-only user can call the GET endpoints only.</summary>
public static class TaskEndpoints
{
    public const string Prefix = "/api/tasks";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var tasks = endpoints.MapGroup(Prefix).WithTags("Tasks");

        tasks.MapPost("", async Task<Created<TaskDto>> (CreateTaskRequest request, TaskService service, CancellationToken cancellationToken) =>
            {
                var task = await service.CreateAsync(request, cancellationToken);
                return TypedResults.Created($"{Prefix}/{task.Id}", task);
            })
            .WithName("CreateTask")
            .WithSummary("Adds a task. Only the title is required.")
            .WithDescription(
                "dueOn is a day (yyyy-MM-dd). repeat is { every, unit (Days, Weeks, Months, Years), mode (FromDueDate, the " +
                "default, or FromCompletion) }; a repeated task without dueOn is due today. links are { kind, targetId } " +
                "(Documents.Document or Money.Transaction); the host checks that each one exists.");

        tasks.MapGet("", (TaskService service, CancellationToken cancellationToken, string? status, string? tag, string? due,
                string? q, int page = 1, int pageSize = 20) =>
                service.ListAsync(new TaskQuery(status, tag, due, q, page, pageSize), cancellationToken))
            .WithName("ListTasks")
            .WithSummary("A page of tasks: by due date (tasks without one last), then the manual order; done tasks: the last done first.")
            .WithDescription(
                "Filters: status (Open, Done), tag, due (today; overdue: open and due before today; upcoming: the next 7 " +
                "days after today; later: after those days; none: no due date), q (words in the title, the notes, or the tags). \"Today\" is in " +
                "the time zone of the host (Tasks:TimeZone).");

        tasks.MapGet("/summary", (TaskService service, CancellationToken cancellationToken) => service.CountAsync(cancellationToken))
            .WithName("GetTaskSummary")
            .WithSummary("The counts of open tasks due today, overdue, and upcoming (7 days), for the badges.");

        tasks.MapGet("/tags", (TaskService service, CancellationToken cancellationToken) => service.TagsAsync(cancellationToken))
            .WithName("ListTaskTags")
            .WithSummary("The tags in use, by name, with the count of tasks (open and done) that have each one.");

        tasks.MapGet("/by-link", (TaskService service, CancellationToken cancellationToken, string? kind, string? id) =>
                service.ListByLinkAsync(kind, id, cancellationToken))
            .WithName("ListTasksByLink")
            .WithSummary("The tasks linked to one thing, for example ?kind=Documents.Document&id=<document id>.");

        tasks.MapGet("/{id:guid}", (Guid id, TaskService service, CancellationToken cancellationToken) =>
                service.GetAsync(id, cancellationToken))
            .WithName("GetTask")
            .WithSummary("One task, with its last 10 completions.");

        tasks.MapPut("/{id:guid}", (Guid id, UpdateTaskRequest request, TaskService service, CancellationToken cancellationToken) =>
                service.UpdateAsync(id, request, cancellationToken))
            .WithName("UpdateTask")
            .WithSummary("Changes the details of a task (all of them). The status, the completions, and the links stay.");

        tasks.MapDelete("/{id:guid}", async Task<NoContent> (Guid id, TaskService service, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteTask")
            .WithSummary("Deletes a task, its completions, and its links.");

        tasks.MapPost("/{id:guid}/done", (Guid id, TaskService service, CancellationToken cancellationToken) =>
                service.DoneAsync(id, cancellationToken))
            .WithName("CompleteTask")
            .WithSummary("Marks a task done. A repeated task stays open, with its next due date.")
            .WithDescription("A task that is done already gets 409.");

        tasks.MapPost("/{id:guid}/undo", (Guid id, TaskService service, CancellationToken cancellationToken) =>
                service.UndoAsync(id, cancellationToken))
            .WithName("UndoTaskDone")
            .WithSummary("Undoes the last \"done\": the task is open again, or a repeated task gets its old due date back.")
            .WithDescription("A task without a completion gets 409.");

        tasks.MapPost("/{id:guid}/links", async Task<Created<TaskLinkDto>> (Guid id, CreateTaskLinkRequest request,
                TaskService service, CancellationToken cancellationToken) =>
            {
                var link = await service.AddLinkAsync(id, request, cancellationToken);
                return TypedResults.Created($"{Prefix}/{id}/links/{link.Id}", link);
            })
            .WithName("AddTaskLink")
            .WithSummary("Links the task to a document or a finance transaction, for example { \"kind\": \"Documents.Document\", \"targetId\": \"<id>\" }.")
            .WithDescription("The host checks that the thing exists. For a transaction it asks finance as the signed-in user (502 when finance does not answer).");

        tasks.MapDelete("/{id:guid}/links/{linkId:guid}", async Task<NoContent> (Guid id, Guid linkId, TaskService service,
                CancellationToken cancellationToken) =>
            {
                await service.RemoveLinkAsync(id, linkId, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("RemoveTaskLink")
            .WithSummary("Removes a link of the task.");
    }
}
