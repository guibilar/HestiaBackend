using Hestia.Modules.Search.Contracts;
using Hestia.Modules.Tasks.Contracts;

namespace Hestia.Modules.Tasks.Application;

/// <summary>The Tasks hits of the search (T17): open and done tasks whose title, notes, or tags have the text.</summary>
public sealed class TaskSearch(TaskService tasks) : ISearchSource
{
    public string Module => TaskDueAlerts.ModuleName;

    public int Order => 110;

    public async Task<SearchHits> SearchAsync(string text, int limit, CancellationToken cancellationToken)
    {
        var page = await tasks.ListAsync(new TaskQuery(null, null, null, text, 1, limit), cancellationToken);
        return new SearchHits(page.Items.Select(t => new SearchHit(
            $"tasks.task.{t.Id:D}", Module, "task", t.Title,
            t.Status == TaskItemStatus.Done ? "Done" : t.Repeat is null ? "Open" : "Open · repeats",
            t.DueOn, null, $"/tasks/{t.Id:D}")).ToList(), page.TotalCount);
    }
}
