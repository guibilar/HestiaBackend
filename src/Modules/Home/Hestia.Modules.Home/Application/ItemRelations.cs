using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.Tasks.Contracts;

namespace Hestia.Modules.Home.Application;

/// <summary>
/// The documents and the tasks of an item, from both sides of the link: the links of the item (kinds
/// <c>Documents.Document</c> and <c>Tasks.Task</c>), and the links of documents and tasks to the item (kind
/// <c>Home.Item</c>). It reads them through the query interfaces of the Documents and Tasks modules.
/// </summary>
public sealed class ItemRelations(IDocumentQueries documents, ITaskQueries tasks)
{
    /// <summary>The kind of a link to an item in the other modules.</summary>
    public const string HomeItemKind = DocumentLinkKinds.HomeItem;

    /// <summary>The documents of an item, by title. A link to a document that is gone is left out.</summary>
    public async Task<List<ItemDocumentDto>> DocumentsAsync(Guid itemId, IEnumerable<ItemLinkDto> links, CancellationToken cancellationToken)
    {
        var found = new Dictionary<Guid, ItemDocumentDto>();
        // One light query for the item's own links (T89): summaries, without the OCR text.
        foreach (var document in await documents.ListAsync(Ids(links, ItemLinkKinds.DocumentsDocument), cancellationToken))
        {
            found[document.Id] = new ItemDocumentDto(document.Id, document.Title, document.Kind.ToString(), document.ExpiresOn);
        }
        foreach (var document in await documents.ListByLinkAsync(HomeItemKind, itemId.ToString("D"), cancellationToken))
        {
            found[document.Id] = new ItemDocumentDto(document.Id, document.Title, document.Kind.ToString(), document.ExpiresOn);
        }
        return found.Values
            .OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Id)
            .ToList();
    }

    /// <summary>The tasks of an item (maintenance): by due date (tasks without one last), then by title.</summary>
    public async Task<List<ItemTaskDto>> TasksAsync(Guid itemId, IEnumerable<ItemLinkDto> links, CancellationToken cancellationToken)
    {
        var found = new Dictionary<Guid, ItemTaskDto>();
        foreach (var task in await tasks.ListAsync(Ids(links, ItemLinkKinds.TasksTask), cancellationToken))
        {
            found[task.Id] = new ItemTaskDto(task.Id, task.Title, task.Status.ToString(), task.DueOn, task.Repeat?.Every,
                task.Repeat?.Unit.ToString(), task.LastDoneAt);
        }
        foreach (var task in await tasks.ListByLinkAsync(TaskLinkKinds.HomeItem, itemId.ToString("D"), cancellationToken))
        {
            found[task.Id] = new ItemTaskDto(task.Id, task.Title, task.Status.ToString(), task.DueOn, task.Repeat?.Every,
                task.Repeat?.Unit.ToString(), task.LastDoneAt);
        }
        return found.Values
            .OrderBy(t => t.DueOn is null)
            .ThenBy(t => t.DueOn)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Id)
            .ToList();
    }

    /// <summary>
    /// The ids of the items that a document or a task links to (kind <c>Home.Item</c>), for <c>GET /items/by-link</c>.
    /// Other kinds give none.
    /// </summary>
    public async Task<List<Guid>> ItemsLinkedFromAsync(string kind, Guid targetId, CancellationToken cancellationToken)
    {
        IEnumerable<string> targets = kind switch
        {
            ItemLinkKinds.DocumentsDocument => (await documents.ListAsync([targetId], cancellationToken))
                .SelectMany(d => d.Links).Where(l => l.Kind == HomeItemKind).Select(l => l.TargetId),
            ItemLinkKinds.TasksTask => (await tasks.ListAsync([targetId], cancellationToken))
                .SelectMany(t => t.Links).Where(l => l.Kind == TaskLinkKinds.HomeItem).Select(l => l.TargetId),
            _ => []
        };
        return targets.Select(t => Guid.TryParse(t, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();
    }

    /// <summary>
    /// All maintenance (T89): each open task with the items it belongs to, from both sides of the link: the tasks that
    /// link to an item, and the tasks that items link to (<paramref name="itemLinks"/>: item id and task id).
    /// </summary>
    public async Task<List<(TaskSummaryDto Task, Guid ItemId)>> MaintenanceAsync(
        IReadOnlyList<(Guid ItemId, string TaskId)> itemLinks, CancellationToken cancellationToken)
    {
        var pairs = new List<(TaskSummaryDto, Guid)>();
        foreach (var task in await tasks.ListOpenWithLinkKindAsync(TaskLinkKinds.HomeItem, cancellationToken))
        {
            foreach (var link in task.Links.Where(l => l.Kind == TaskLinkKinds.HomeItem))
            {
                if (Guid.TryParse(link.TargetId, out var itemId))
                {
                    pairs.Add((task, itemId));
                }
            }
        }
        var taskIds = itemLinks.Select(l => Guid.TryParse(l.TaskId, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToHashSet();
        var linked = (await tasks.ListAsync(taskIds, cancellationToken)).Where(t => t.Status == TaskItemStatus.Open).ToDictionary(t => t.Id);
        foreach (var (itemId, taskId) in itemLinks)
        {
            if (Guid.TryParse(taskId, out var id) && linked.TryGetValue(id, out var task))
            {
                pairs.Add((task, itemId));
            }
        }
        return pairs.DistinctBy(p => (p.Item1.Id, p.Item2)).ToList();
    }

    /// <summary>The ids of the links of one kind (a link with a target that is not a GUID is left out).</summary>
    private static HashSet<Guid> Ids(IEnumerable<ItemLinkDto> links, string kind) =>
        links.Where(l => l.Kind == kind)
            .Select(l => Guid.TryParse(l.TargetId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
}
