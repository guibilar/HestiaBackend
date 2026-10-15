using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Hestia.Modules.Search.Contracts;
using Hestia.Testing;

namespace Hestia.Modules.Search.Tests;

/// <summary>
/// T17: one search across all modules. Each module searches its own data (D15: PostgreSQL full-text search where it has
/// an index); Money searches the descriptions of the transactions in finance, as the signed-in user.
/// </summary>
public class SearchTests
{
    private sealed class Host : IAsyncDisposable
    {
        public required HestiaApiFactory Factory { get; init; }
        public required FakeFinance Finance { get; init; }
        public HttpClient Client => _client ??= Factory.CreateClient();
        private HttpClient? _client;

        public async Task<SearchDto> SearchAsync(string query)
        {
            var response = await Client.GetAsync($"/api/search{query}");
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return await response.ReadAsync<SearchDto>();
        }

        public async Task<T> PostAsync<T>(string url, object body)
        {
            var response = await Client.PostAsJsonAsync(url, body, HestiaApiFactory.Json);
            Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync()}");
            return await response.ReadAsync<T>();
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            await Factory.DisposeAsync();
            await Finance.DisposeAsync();
        }
    }

    private sealed record Created(Guid Id);

    private static async Task<Host> StartAsync(Action<FinanceData>? finance = null)
    {
        var data = new FinanceData();
        data.Answers["POST /api/Transaction/Filter"] = new
        {
            items = new object[]
            {
                new
                {
                    id = "0199b0b0-0000-7000-8000-000000000003", date = "2025-09-08T00:00:00Z", description = "HOME DEPOT DISHWASHER",
                    amount = -899m, category = new { id = Guid.NewGuid(), name = "Household" }, accountName = "CIBC Visa"
                }
            },
            page = 1, pageSize = 5, totalCount = 3
        };
        finance?.Invoke(data);
        var fake = await FakeFinance.StartAsync(data.RespondAsync);
        var factory = new HestiaApiFactory(fake.HostSettings(new Dictionary<string, string?> { ["Documents:Ocr:Enabled"] = "false" }));
        return new Host { Factory = factory, Finance = fake };
    }

    private static async Task SeedAsync(Host host)
    {
        var room = await host.PostAsync<Created>("/api/home/rooms", new { name = "Kitchen" });
        await host.PostAsync<Created>("/api/home/items", new { name = "Dishwasher", roomId = room.Id, purchasedOn = "2025-09-08" });
        await host.PostAsync<Created>("/api/tasks", new { title = "Clean the dishwasher filter", dueOn = "2026-11-01" });
        await host.PostAsync<Created>("/api/tasks", new { title = "Call the plumber" });
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Dishwasher warranty"), "title");
        form.Add(new StringContent("Warranty"), "kind");
        var file = new ByteArrayContent("%PDF-1.4\n%%EOF\n"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "files", "warranty.pdf");
        Assert.True((await host.Client.PostAsync("/api/documents", form)).IsSuccessStatusCode);
        await host.PostAsync<object>("/api/people", new { name = "Ana Lima", relationship = "Neighbour", notes = "Fixed our dishwasher" });
        var today = await host.Client.GetFromJsonAsync<Dictionary<string, object>>("/api/journal/summary");
        var put = await host.Client.PutAsJsonAsync($"/api/journal/entries/{today!["date"]}", new { text = "The dishwasher came today." });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OneSearch_FindsTheThingsOfEveryModule_InModuleOrder()
    {
        await using var host = await StartAsync();
        await SeedAsync(host);

        var result = await host.SearchAsync("?q=%20dishwasher%20");

        Assert.Equal("dishwasher", result.Query);
        Assert.Equal(["Money", "Documents", "Tasks", "Home", "People", "Journal", "Inbox"], result.Modules.Select(m => m.Module));
        Assert.All(result.Modules, m => Assert.Equal(SearchModuleStatus.Complete, m.Status));
        // Finance says 3 matches in all, and gave the first one.
        var money = result.Modules[0];
        Assert.Equal(3, money.Total);
        var transaction = Assert.Single(money.Hits);
        Assert.Equal(("HOME DEPOT DISHWASHER", "Household · CIBC Visa", (DateOnly?)new DateOnly(2025, 9, 8), (decimal?)-899m,
            "/money/transactions?id=0199b0b0-0000-7000-8000-000000000003"),
            (transaction.Title, transaction.Text, transaction.Day, transaction.Amount, transaction.Link));
        Assert.Equal(8, result.Total);

        Assert.Equal(("Dishwasher warranty", "Warranty"), Single(result, "Documents").Pipe(h => (h.Title, h.Text)));
        Assert.Equal(("Clean the dishwasher filter", "Open", (DateOnly?)new DateOnly(2026, 11, 1)), Single(result, "Tasks").Pipe(h => (h.Title, h.Text, h.Day)));
        Assert.Equal(("Dishwasher", "Kitchen"), Single(result, "Home").Pipe(h => (h.Title, h.Text)));
        Assert.Equal(("Ana Lima", "Neighbour"), Single(result, "People").Pipe(h => (h.Title, h.Text)));
        Assert.Equal("The dishwasher came today.", Single(result, "Journal").Text);
        Assert.All(result.Modules.SelectMany(m => m.Hits), h => Assert.StartsWith("/", h.Link));

        // Finance got the text as the description, as the signed-in user, with the limit of a module.
        var request = host.Finance.Requests.Single(r => r.Target == "/api/Transaction/Filter");
        Assert.Contains("\"description\":\"dishwasher\"", request.Body);
        Assert.Contains("\"pageSize\":5", request.Body);
        Assert.Equal(TestSignIn.UserId, GatewayToken.SubjectOf(request));
    }

    [Fact]
    public async Task OneModule_GetsMoreHits()
    {
        await using var host = await StartAsync();
        await SeedAsync(host);
        for (var i = 0; i < 6; i++)
        {
            await host.PostAsync<Created>("/api/tasks", new { title = $"Dishwasher job {i}" });
        }

        var all = await host.SearchAsync("?q=dishwasher");
        var tasks = await host.SearchAsync("?q=dishwasher&module=tasks");

        Assert.Equal((5, 7), (Hits(all, "Tasks").Count, all.Modules.Single(m => m.Module == "Tasks").Total));
        var only = Assert.Single(tasks.Modules);
        Assert.Equal(("Tasks", 7, 7), (only.Module, only.Total, only.Hits.Count));
        Assert.Empty(host.Finance.Requests.Where(r => r.Target == "/api/Transaction/Filter").Skip(1));
    }

    [Fact]
    public async Task FinanceDown_FailsMoneyOnly()
    {
        await using var host = await StartAsync(data => data.Failures["POST /api/Transaction/Filter"] = 502);
        await SeedAsync(host);

        var result = await host.SearchAsync("?q=dishwasher");

        Assert.Equal((SearchModuleStatus.Failed, 0), result.Modules[0].Pipe(m => (m.Status, m.Total)));
        Assert.All(result.Modules.Skip(1), m => Assert.Equal(SearchModuleStatus.Complete, m.Status));
        Assert.Equal(5, result.Total);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?q=a")]
    [InlineData("?q=%20%20d%20%20")]
    [InlineData("?q=dishwasher&limit=0")]
    [InlineData("?q=dishwasher&limit=51")]
    [InlineData("?q=dishwasher&module=Recipes")]
    public async Task BadParameters_Are400(string query)
    {
        await using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync($"/api/search{query}")).StatusCode);
    }

    [Fact]
    public async Task ALongText_Is400_AndAReadOnlyUser_Searches()
    {
        await using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync($"/api/search?q={new string('a', 201)}")).StatusCode);
        host.Client.DefaultRequestHeaders.Add(TestSignIn.ReadOnlyHeader, "yes");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/search?q=plumber")).StatusCode);
    }

    private static SearchHit Single(SearchDto result, string module) =>
        Assert.Single(result.Modules.Single(m => m.Module == module).Hits);

    private static IReadOnlyList<SearchHit> Hits(SearchDto result, string module) =>
        result.Modules.Single(m => m.Module == module).Hits;
}

internal static class PipeExtensions
{
    public static TResult Pipe<T, TResult>(this T value, Func<T, TResult> map) => map(value);
}
