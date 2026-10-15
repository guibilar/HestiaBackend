using Hestia.Modules.Tasks.Contracts;
using Hestia.Modules.Tasks.Domain;
using Hestia.Modules.Tasks.Infrastructure;
using Hestia.Platform.Exceptions;
using Hestia.Platform.Paging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Tasks.Application;

/// <summary>The filters of the task list.</summary>
/// <param name="Status">Open or Done, or null for both.</param>
/// <param name="Tag">One tag, or null.</param>
/// <param name="Due"><c>today</c>, <c>overdue</c>, <c>upcoming</c>, <c>later</c>, or <c>none</c>, or null for all.</param>
/// <param name="Text">Words that must be in the title, the notes, or the tags.</param>
/// <param name="Page">The page, from 1.</param>
/// <param name="PageSize">The page size, 1 to 100.</param>
public sealed record TaskQuery(string? Status, string? Tag, string? Due, string? Text, int Page, int PageSize);

public sealed class TaskService(
    TasksDbContext db,
    TaskLinkTargets linkTargets,
    TaskDays days,
    IOptions<TasksOptions> options,
    ILogger<TaskService> logger)
{
    public const int MaxSearchLength = 200;
    public const int MaxLinksPerRequest = 20;

    /// <summary>The completions in <c>GET /api/tasks/{id}</c>.</summary>
    public const int CompletionsShown = 10;

    public async Task<TaskDto> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken)
    {
        var details = TaskDetails.Create(request.Title, request.Notes, request.DueOn, request.Tags, request.Repeat,
            request.SortOrder, days.Today);
        var linkRequests = request.Links ?? [];
        if (linkRequests.Count > MaxLinksPerRequest)
        {
            throw new InvalidInputException("links", $"Add at most {MaxLinksPerRequest} links at once.");
        }
        // All links are checked before anything is saved.
        var targets = new List<(string Kind, string TargetId)>();
        foreach (var link in linkRequests)
        {
            var target = await linkTargets.CheckAsync(link?.Kind, link?.TargetId, cancellationToken);
            if (!targets.Contains(target))
            {
                targets.Add(target);
            }
        }

        var now = days.Now;
        var task = new TaskItem
        {
            Id = Guid.CreateVersion7(),
            Status = TaskItemStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        };
        details.ApplyTo(task);
        db.Tasks.Add(task);
        foreach (var (kind, targetId) in targets)
        {
            db.Links.Add(new TaskLink { Id = Guid.CreateVersion7(), TaskId = task.Id, Kind = kind, TargetId = targetId, CreatedAt = now });
        }
        db.RaiseEvent(new TaskCreated(task.Id, task.Title, task.DueOn, details.Tags, task.Repeat, now));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Tasks: added task {TaskId}", task.Id);
        return await GetAsync(task.Id, cancellationToken);
    }

    public async Task<PaginatedList<TaskSummaryDto>> ListAsync(TaskQuery query, CancellationToken cancellationToken)
    {
        PagingExtensions.Validate(query.Page, query.PageSize);
        IQueryable<TaskItem> tasks = db.Tasks.AsNoTracking();

        var listStatus = ParseStatus(query.Status);
        if (listStatus is { } status)
        {
            tasks = tasks.Where(t => t.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tags = TaskTags.Normalize([query.Tag]);
            if (tags.Count != 1)
            {
                throw new InvalidInputException("tag", "Give one tag.");
            }
            var pattern = TaskTags.Pattern(tags[0]);
            tasks = tasks.Where(t => t.Tags.Contains(pattern));
        }
        tasks = FilterByDue(tasks, query.Due);

        var text = query.Text?.Trim() ?? string.Empty;
        if (text.Length > MaxSearchLength)
        {
            throw new InvalidInputException("q", $"The search text can have at most {MaxSearchLength} characters.");
        }
        // Each word must be in the title, the notes, or the tags (any letter case).
        foreach (var word in text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            tasks = tasks.Where(t => t.Title.ToLower().Contains(word) || t.Tags.Contains(word) ||
                (t.Notes != null && t.Notes.ToLower().Contains(word)));
        }

        var total = await tasks.CountAsync(cancellationToken);
        // The done tasks: the last one done first (T86). The other lists: by due date.
        var ordered = listStatus == TaskItemStatus.Done ? InDoneOrder(tasks) : InListOrder(tasks);
        var items = await SummariesAsync(ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize), cancellationToken);
        return new PaginatedList<TaskSummaryDto> { Items = items, Page = query.Page, PageSize = query.PageSize, TotalCount = total };
    }

    /// <summary>The list order: by due date (tasks without one last), then the manual order, then the oldest first.</summary>
    private static IQueryable<TaskItem> InListOrder(IQueryable<TaskItem> tasks) => tasks
        .OrderBy(t => t.DueOn == null)
        .ThenBy(t => t.DueOn)
        .ThenBy(t => t.SortOrder == null)
        .ThenBy(t => t.SortOrder)
        .ThenBy(t => t.CreatedAt)
        .ThenBy(t => t.Id);

    /// <summary>The order of the done tasks (T86): the last one done first, then the newest first.</summary>
    private static IQueryable<TaskItem> InDoneOrder(IQueryable<TaskItem> tasks) => tasks
        .OrderByDescending(t => t.DoneAt)
        .ThenByDescending(t => t.CreatedAt)
        .ThenByDescending(t => t.Id);

    /// <summary>The tags in use (T86), by name, with the count of tasks (open and done) that have each one.</summary>
    public async Task<List<TagCountDto>> TagsAsync(CancellationToken cancellationToken)
    {
        var stored = await db.Tasks.AsNoTracking().Where(t => t.Tags != "").Select(t => t.Tags).ToListAsync(cancellationToken);
        return stored
            .SelectMany(TaskTags.Read)
            .GroupBy(tag => tag)
            .Select(group => new TagCountDto(group.Key, group.Count()))
            .OrderBy(tag => tag.Name, StringComparer.Ordinal)
            .ToList();
    }

    private IQueryable<TaskItem> FilterByDue(IQueryable<TaskItem> tasks, string? due)
    {
        if (string.IsNullOrWhiteSpace(due))
        {
            return tasks;
        }
        var today = days.Today;
        var last = today.AddDays(options.Value.UpcomingDays);
        return due.Trim().ToLowerInvariant() switch
        {
            "today" => tasks.Where(t => t.DueOn == today),
            // A done task is not late.
            "overdue" => tasks.Where(t => t.Status == TaskItemStatus.Open && t.DueOn < today),
            "upcoming" => tasks.Where(t => t.DueOn > today && t.DueOn <= last),
            // After the upcoming days (T86).
            "later" => tasks.Where(t => t.DueOn > last),
            "none" => tasks.Where(t => t.DueOn == null),
            _ => throw new InvalidInputException("due", "Use due=today, overdue, upcoming, later, or none.")
        };
    }

    private static TaskItemStatus? ParseStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var text = value.Trim();
        if (!int.TryParse(text, out _) && Enum.TryParse<TaskItemStatus>(text, ignoreCase: true, out var status) && Enum.IsDefined(status))
        {
            return status;
        }
        throw new InvalidInputException("status", $"\"{text}\" is not a status. Use one of: {string.Join(", ", Enum.GetNames<TaskItemStatus>())}.");
    }

    public async Task<TaskDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await FindAsync(id, cancellationToken) ?? throw new NotFoundException("Task", id);

    public async Task<TaskDto?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var summary = (await SummariesAsync(db.Tasks.AsNoTracking().Where(t => t.Id == id), cancellationToken)).SingleOrDefault();
        if (summary is null)
        {
            return null;
        }
        var completions = await db.Completions.AsNoTracking()
            .Where(c => c.TaskId == id)
            .OrderByDescending(c => c.DoneAt)
            .ThenByDescending(c => c.Id)
            .Take(CompletionsShown)
            .Select(c => new TaskCompletionDto(c.Id, c.DoneAt, c.DueOn))
            .ToListAsync(cancellationToken);
        return new TaskDto(summary.Id, summary.Title, summary.Notes, summary.DueOn, summary.Tags, summary.Status, summary.DoneAt,
            summary.Repeat, summary.SortOrder, summary.LastDoneAt, summary.CreatedAt, summary.UpdatedAt, summary.Links, completions);
    }

    public async Task<TaskDto> UpdateAsync(Guid id, UpdateTaskRequest request, CancellationToken cancellationToken)
    {
        var details = TaskDetails.Create(request.Title, request.Notes, request.DueOn, request.Tags, request.Repeat,
            request.SortOrder, days.Today);
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException("Task", id);
        var now = days.Now;
        details.ApplyTo(task);
        task.UpdatedAt = now;
        db.RaiseEvent(new TaskChanged(task.Id, TaskChange.Details, task.Title, task.DueOn, details.Tags, task.Repeat, task.Status, now));
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await db.Tasks.Include(t => t.Completions).Include(t => t.Links)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException("Task", id);
        db.RaiseEvent(new TaskDeleted(task.Id, task.Title, days.Now));
        // The completions and the links go with the task (cascade).
        db.Tasks.Remove(task);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Tasks: deleted task {TaskId}", id);
    }

    /// <summary>
    /// Marks the task done. A task without a repeat becomes <see cref="TaskItemStatus.Done"/>. A repeated task stays open,
    /// and its due date moves to the next date of the repeat (<see cref="TaskSchedule.Next"/>). Both get a completion.
    /// </summary>
    public async Task<TaskDto> DoneAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException("Task", id);
        if (task.Status == TaskItemStatus.Done)
        {
            throw new ConflictException("The task is done already.");
        }
        var now = days.Now;
        var completion = new TaskCompletion { Id = Guid.CreateVersion7(), TaskId = task.Id, DoneAt = now, DueOn = task.DueOn };
        DateOnly? next = null;
        if (task.Repeat is { } repeat && task.DueOn is { } due)
        {
            try
            {
                next = TaskSchedule.Next(due, days.DayOf(now), repeat, task.RepeatDay);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new ConflictException("The repeat has no next date before the year 10000.");
            }
            task.DueOn = next;
        }
        else
        {
            task.Status = TaskItemStatus.Done;
            task.DoneAt = now;
        }
        task.UpdatedAt = now;
        db.Completions.Add(completion);
        db.RaiseEvent(new TaskCompleted(task.Id, completion.Id, task.Title, completion.DueOn, next, now));
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    /// <summary>
    /// Undoes the last "done": a done task is open again; a repeated task gets back the due date of its last completion.
    /// The last completion is removed. Only the last one can be undone, one at a time.
    /// </summary>
    public async Task<TaskDto> UndoAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException("Task", id);
        var last = await db.Completions
            .Where(c => c.TaskId == id)
            .OrderByDescending(c => c.DoneAt)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (last is null)
        {
            throw new ConflictException("The task has no \"done\" to undo.");
        }
        if (task.Status == TaskItemStatus.Done)
        {
            task.Status = TaskItemStatus.Open;
            task.DoneAt = null;
        }
        else
        {
            // A repeated task: the due date goes back to the date that was done.
            task.DueOn = last.DueOn ?? task.DueOn;
        }
        var now = days.Now;
        task.UpdatedAt = now;
        db.Completions.Remove(last);
        db.RaiseEvent(new TaskReopened(task.Id, last.Id, task.Title, task.DueOn, now));
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<TaskLinkDto> AddLinkAsync(Guid id, CreateTaskLinkRequest request, CancellationToken cancellationToken)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException("Task", id);
        var (kind, targetId) = await linkTargets.CheckAsync(request.Kind, request.TargetId, cancellationToken);
        if (await db.Links.AnyAsync(l => l.TaskId == id && l.Kind == kind && l.TargetId == targetId, cancellationToken))
        {
            throw new ConflictException("The task has this link already.");
        }

        var now = days.Now;
        var link = new TaskLink { Id = Guid.CreateVersion7(), TaskId = id, Kind = kind, TargetId = targetId, CreatedAt = now };
        db.Links.Add(link);
        task.UpdatedAt = now;
        db.RaiseEvent(Changed(task, TaskChange.Links, now));
        await db.SaveChangesAsync(cancellationToken);
        return new TaskLinkDto(link.Id, link.Kind, link.TargetId, link.CreatedAt);
    }

    public async Task RemoveLinkAsync(Guid id, Guid linkId, CancellationToken cancellationToken)
    {
        var link = await db.Links.FirstOrDefaultAsync(l => l.Id == linkId && l.TaskId == id, cancellationToken)
            ?? throw new NotFoundException("Link", linkId);
        var task = await db.Tasks.FirstAsync(t => t.Id == id, cancellationToken);
        var now = days.Now;
        db.Links.Remove(link);
        task.UpdatedAt = now;
        db.RaiseEvent(Changed(task, TaskChange.Links, now));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskSummaryDto>> ListByLinkAsync(string? kind, string? targetId, CancellationToken cancellationToken)
    {
        var (linkKind, target) = TaskLinkTargets.Normalize(kind, targetId);
        var tasks = db.Tasks.AsNoTracking().Where(t => t.Links.Any(l => l.Kind == linkKind && l.TargetId == target));
        return await SummariesAsync(InListOrder(tasks), cancellationToken);
    }

    /// <summary>The counts of open tasks for the badges: due today, overdue, and upcoming.</summary>
    public async Task<TaskCountsDto> CountAsync(CancellationToken cancellationToken)
    {
        var today = days.Today;
        var last = today.AddDays(options.Value.UpcomingDays);
        var open = db.Tasks.AsNoTracking().Where(t => t.Status == TaskItemStatus.Open);
        var dueToday = await open.CountAsync(t => t.DueOn == today, cancellationToken);
        var overdue = await open.CountAsync(t => t.DueOn < today, cancellationToken);
        var upcoming = await open.CountAsync(t => t.DueOn > today && t.DueOn <= last, cancellationToken);
        return new TaskCountsDto(today, dueToday, overdue, upcoming);
    }

    private static TaskChanged Changed(TaskItem task, TaskChange change, DateTimeOffset now) =>
        new(task.Id, change, task.Title, task.DueOn, TaskTags.Read(task.Tags), task.Repeat, task.Status, now);

    /// <summary>Reads the tasks of an (ordered) query with their links and the time of the last completion.</summary>
    private static async Task<List<TaskSummaryDto>> SummariesAsync(IQueryable<TaskItem> tasks, CancellationToken cancellationToken)
    {
        var rows = await tasks
            .Select(t => new
            {
                Item = t,
                Links = t.Links.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
                    .Select(l => new TaskLinkDto(l.Id, l.Kind, l.TargetId, l.CreatedAt)).ToList(),
                LastDoneAt = t.Completions.Max(c => (DateTimeOffset?)c.DoneAt)
            })
            .ToListAsync(cancellationToken);
        return rows.Select(row => new TaskSummaryDto(row.Item.Id, row.Item.Title, row.Item.Notes, row.Item.DueOn,
                TaskTags.Read(row.Item.Tags), row.Item.Status, row.Item.DoneAt, row.Item.Repeat, row.Item.SortOrder, row.LastDoneAt,
                row.Item.CreatedAt, row.Item.UpdatedAt, row.Links))
            .ToList();
    }
}

/// <summary>The other modules' queries (<see cref="ITaskQueries"/>).</summary>
public sealed class TaskQueries(TaskService tasks) : ITaskQueries
{
    public Task<TaskDto?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        tasks.FindAsync(id, cancellationToken);

    public async Task<IReadOnlyList<TaskSummaryDto>> ListByLinkAsync(string kind, string targetId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await tasks.ListByLinkAsync(kind, targetId, cancellationToken);
        }
        catch (InvalidInputException)
        {
            return [];
        }
    }
}
