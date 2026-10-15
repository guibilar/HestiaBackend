using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.Inbox.Application;
using Hestia.Modules.Inbox.Contracts;
using Hestia.Modules.Search.Contracts;
using Hestia.Modules.Today.Contracts;
using Hestia.Platform.Paging;
using Hestia.Testing;

namespace Hestia.Modules.Inbox.Tests;

/// <summary>T18: quick notes kept to sort later; the clients sort a note into another module, then mark it done.</summary>
public class InboxTests
{
    private const string Inbox = "/api/inbox";

    private static async Task<InboxItemDto> AddAsync(HttpClient client, string text)
    {
        var response = await client.PostAsJsonAsync(Inbox, new InboxTextRequest(text), HestiaApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<InboxItemDto>();
    }

    private static async Task<PaginatedList<InboxItemDto>> ListAsync(HttpClient client, string query = "") =>
        await (await client.GetAsync($"{Inbox}{query}")).ReadAsync<PaginatedList<InboxItemDto>>();

    [Fact]
    public async Task ANote_IsAdded_Changed_Sorted_Reopened_AndDeleted()
    {
        await using var factory = new HestiaApiFactory();
        var client = factory.CreateClient();

        var first = await AddAsync(client, "  Call the plumber about the leak  ");
        var second = await AddAsync(client, "https://example.com/recipe");
        Assert.Equal(("Call the plumber about the leak", (DateTimeOffset?)null, (string?)null), (first.Text, first.DoneAt, first.SortedTo));

        var open = await ListAsync(client);
        Assert.Equal([second.Id, first.Id], open.Items.Select(i => i.Id));

        var changed = await (await client.PutAsJsonAsync($"{Inbox}/{first.Id}", new InboxTextRequest("Call the plumber"), HestiaApiFactory.Json))
            .ReadAsync<InboxItemDto>();
        Assert.Equal("Call the plumber", changed.Text);

        var sorted = await (await client.PostAsJsonAsync($"{Inbox}/{first.Id}/done", new InboxDoneRequest(" Task "), HestiaApiFactory.Json))
            .ReadAsync<InboxItemDto>();
        Assert.Equal("Task", sorted.SortedTo);
        Assert.NotNull(sorted.DoneAt);
        // Sorted twice: the first time and place stay. (Compare with the stored time: PostgreSQL keeps microseconds.)
        var stored = await (await client.GetAsync($"{Inbox}/{first.Id}")).ReadAsync<InboxItemDto>();
        var again = await (await client.PostAsJsonAsync($"{Inbox}/{first.Id}/done", new InboxDoneRequest("Journal"), HestiaApiFactory.Json))
            .ReadAsync<InboxItemDto>();
        Assert.Equal((stored.DoneAt, "Task"), (again.DoneAt, again.SortedTo));
        Assert.Equal([second.Id], (await ListAsync(client)).Items.Select(i => i.Id));
        Assert.Equal([first.Id], (await ListAsync(client, "?done=true")).Items.Select(i => i.Id));

        // A done without a body: sorted to nowhere.
        var nowhere = await (await client.PostAsync($"{Inbox}/{second.Id}/done", null)).ReadAsync<InboxItemDto>();
        Assert.Null(nowhere.SortedTo);

        var reopened = await (await client.PostAsync($"{Inbox}/{first.Id}/reopen", null)).ReadAsync<InboxItemDto>();
        Assert.Equal(((DateTimeOffset?)null, (string?)null), (reopened.DoneAt, reopened.SortedTo));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Inbox}/{first.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Inbox}/{first.Id}")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ANoteWithoutText_IsRefused(string text)
    {
        await using var factory = new HestiaApiFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Inbox, new InboxTextRequest(text), HestiaApiFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(Inbox, new InboxTextRequest(new string('a', InboxLimits.MaxTextLength + 1)), HestiaApiFactory.Json)).StatusCode);
    }

    [Fact]
    public async Task Today_CountsTheNotesToSort_AndTheSearch_FindsThem()
    {
        await using var factory = new HestiaApiFactory();
        var client = factory.CreateClient();
        await AddAsync(client, "Buy filters for the furnace\nThe 16x25 ones");
        var sorted = await AddAsync(client, "Furnace guy called back");
        await client.PostAsync($"{Inbox}/{sorted.Id}/done", null);

        var today = await (await client.GetAsync("/api/today?refresh=true")).ReadAsync<TodayDto>();
        var tile = Assert.Single(today.Cards, c => c.Id == InboxTodayCards.TileId);
        Assert.Equal((1m, "to sort", "/inbox"), (tile.Value!.Number, tile.Text, tile.Link));

        var search = await (await client.GetAsync("/api/search?q=furnace&module=Inbox")).ReadAsync<SearchDto>();
        var hit = Assert.Single(Assert.Single(search.Modules).Hits);
        Assert.Equal(("Buy filters for the furnace", "note", "/inbox"), (hit.Title, hit.Kind, hit.Link));
    }

    [Fact]
    public async Task AReadOnlyUser_ReadsOnly()
    {
        await using var factory = new HestiaApiFactory();
        var client = factory.CreateClient();
        var note = await AddAsync(client, "Mine");
        client.DefaultRequestHeaders.Add(TestSignIn.ReadOnlyHeader, "yes");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Inbox)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Inbox, new InboxTextRequest("x"), HestiaApiFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{Inbox}/{note.Id}/done", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{Inbox}/{note.Id}")).StatusCode);
    }

    [Fact]
    public void ASearchTitle_IsTheFirstLine()
    {
        Assert.Equal("Buy filters", InboxSearch.Title("Buy filters\nmore"));
        Assert.Equal(120, InboxSearch.Title(new string('x', 200)).Length);
    }
}
