using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.Inbox.Contracts;
using Hestia.Modules.Search.Contracts;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Platform.Paging;
using Hestia.Testing;

namespace Hestia.Api.Tests;

/// <summary>
/// PostgreSQL cannot keep the NUL character in text, so a body or a search with it was a 500 in every module. The host
/// removes it from JSON strings and from the query string (Hestia.Platform.Http.NulCharacters).
/// </summary>
public class NulCharacterTests
{
    [Fact]
    public async Task ANulInTheBody_IsRemoved_AndTheWriteWorks()
    {
        await using var factory = new HestiaApiFactory();
        var client = factory.CreateClient();

        var note = await client.PostAsJsonAsync("/api/inbox", new InboxTextRequest("Call\0 the plumber"), HestiaApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, note.StatusCode);
        Assert.Equal("Call the plumber", (await note.ReadAsync<InboxItemDto>()).Text);

        var task = await client.PostAsJsonAsync("/api/tasks", new { title = "\0Water\0 the plants", notes = "\0" }, HestiaApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, task.StatusCode);
        Assert.Equal("Water the plants", (await task.ReadAsync<TaskDto>()).Title);
    }

    [Fact]
    public async Task ANulInTheQuery_IsRemoved_AndTheSearchWorks()
    {
        await using var factory = new HestiaApiFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/inbox", new InboxTextRequest("Call the plumber"), HestiaApiFactory.Json);

        var list = await client.GetAsync("/api/inbox?q=plum%00ber");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Single((await list.ReadAsync<PaginatedList<InboxItemDto>>()).Items);

        var search = await (await client.GetAsync("/api/search?q=plum%00ber&module=Inbox")).ReadAsync<SearchDto>();
        Assert.Equal((SearchModuleStatus.Complete, 1), (search.Modules.Single().Status, search.Total));
    }
}
