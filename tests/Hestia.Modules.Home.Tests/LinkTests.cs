using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Home.Application;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Platform.Events;
using Hestia.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Home.Tests;

/// <summary>
/// Links from an item to a document, a task, or a finance transaction (T22), and the links of documents and tasks to an
/// item (kind <c>Home.Item</c>). The host checks a document and a task with their modules, and a transaction with finance
/// (<see cref="FakeFinance"/> here), as the signed-in user.
/// </summary>
public class LinkTests
{
    private const string Items = HomeHost.Items;
    private const string DocumentKind = ItemLinkKinds.DocumentsDocument;
    private const string TaskKind = ItemLinkKinds.TasksTask;
    private const string MoneyKind = ItemLinkKinds.MoneyTransaction;
    private const string HomeItem = "Home.Item";

    /// <summary>Finance's transaction filter: it finds the transactions in <see cref="Known"/>.</summary>
    private sealed class Transactions
    {
        public HashSet<Guid> Known { get; } = [];
        public int Status { get; set; } = StatusCodes.Status200OK;
        public FakeFinance Finance { get; set; } = null!;

        public async Task RespondAsync(HttpContext context)
        {
            if (context.Request.Path == "/health/ready")
            {
                await context.Response.WriteAsync("Healthy");
                return;
            }
            if (Status != StatusCodes.Status200OK)
            {
                context.Response.StatusCode = Status;
                return;
            }
            var body = Finance.Requests.Last(request => request.Target == "/api/Transaction/Filter").Body;
            var id = JsonDocument.Parse(body).RootElement.GetProperty("transactionId").GetGuid();
            await context.Response.WriteAsJsonAsync(new { items = Array.Empty<object>(), page = 1, pageSize = 1, totalCount = Known.Contains(id) ? 1 : 0 });
        }
    }

    private static async Task<(HomeHost Host, FakeFinance Finance, Transactions Transactions)> StartAsync()
    {
        var transactions = new Transactions();
        var finance = await FakeFinance.StartAsync(transactions.RespondAsync);
        transactions.Finance = finance;
        return (HomeHost.Start(finance.HostSettings()), finance, transactions);
    }

    private static async Task<TaskDto> CreateTaskAsync(HomeHost host, CreateTaskRequest request)
    {
        var response = await host.Client.PostAsJsonAsync("/api/tasks", request, HestiaApiFactory.Json);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<TaskDto>();
    }

    [Fact]
    public async Task ALink_ToADocument_IsChecked_Stored_AndShownWithTheItem()
    {
        var (host, finance, _) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var receipt = await host.UploadDocumentAsync("Dishwasher receipt");
        var item = await host.CreateAsync("Dishwasher");

        var response = await host.Client.PostAsJsonAsync($"{Items}/{item.Id}/links",
            new CreateItemLinkRequest("documents.document", receipt.Id.ToString("D").ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var link = await response.ReadAsync<ItemLinkDto>();
        Assert.Equal((DocumentKind, receipt.Id.ToString("D")), (link.Kind, link.TargetId));
        Assert.Equal($"{Items}/{item.Id}/links/{link.Id}", response.Headers.Location?.OriginalString);
        Assert.Empty(finance.Requests);
        var changed = await host.Factory.Events.WaitForAsync<ItemChanged>(e => e.ItemId == item.Id && e.Change == ItemChange.Links);
        Assert.Equal("Dishwasher", changed.Name);
        var after = await host.GetAsync(item.Id);
        Assert.Equal(link.Id, Assert.Single(after.Links).Id);
        Assert.Equal(new ItemDocumentDto(receipt.Id, "Dishwasher receipt", "Other", null), Assert.Single(after.Documents));

        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsJsonAsync($"{Items}/{item.Id}/links",
            new CreateItemLinkRequest(DocumentKind, receipt.Id.ToString()))).StatusCode);
    }

    [Fact]
    public async Task ALink_ToATransaction_IsCheckedWithFinance_AsTheSignedInUser()
    {
        var (host, finance, transactions) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var transactionId = Guid.NewGuid();
        transactions.Known.Add(transactionId);
        var item = await host.CreateAsync("Laptop");

        var response = await host.Client.PostAsJsonAsync($"{Items}/{item.Id}/links", new CreateItemLinkRequest(MoneyKind, transactionId.ToString()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var request = Assert.Single(finance.Requests);
        Assert.Equal(("POST", "/api/Transaction/Filter"), (request.Method, request.Target));
        Assert.Equal(transactionId, JsonDocument.Parse(request.Body).RootElement.GetProperty("transactionId").GetGuid());
        Assert.Equal(TestSignIn.UserId, GatewayToken.SubjectOf(request));
    }

    [Fact]
    public async Task ALink_ThatCannotBeMade_IsABadRequest_OrNotFound_And502WhenFinanceIsDown()
    {
        var (host, finance, transactions) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var item = await host.CreateAsync("Laptop");
        var links = $"{Items}/{item.Id}/links";

        await HomeHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateItemLinkRequest(DocumentKind, Guid.NewGuid().ToString())), "targetId");
        await HomeHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateItemLinkRequest(TaskKind, Guid.NewGuid().ToString())), "targetId");
        await HomeHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateItemLinkRequest(MoneyKind, Guid.NewGuid().ToString())), "targetId");
        await HomeHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateItemLinkRequest(TaskKind, "not-a-guid")), "targetId");
        await HomeHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateItemLinkRequest(HomeItem, Guid.NewGuid().ToString())), "kind");
        await HomeHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateItemLinkRequest(null, "1")), "kind");
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsJsonAsync($"{Items}/{Guid.NewGuid()}/links",
            new CreateItemLinkRequest(DocumentKind, Guid.NewGuid().ToString()))).StatusCode);

        transactions.Status = StatusCodes.Status503ServiceUnavailable;
        Assert.Equal(HttpStatusCode.BadGateway, (await host.Client.PostAsJsonAsync(links,
            new CreateItemLinkRequest(MoneyKind, Guid.NewGuid().ToString()))).StatusCode);
        Assert.Empty((await host.GetAsync(item.Id)).Links);
    }

    [Fact]
    public async Task Create_WithLinks_ChecksThemAll_BeforeItStoresTheItem()
    {
        var (host, finance, _) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var manual = await host.UploadDocumentAsync("Manual");

        await HomeHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(Items, new CreateItemRequest("Oven", Links:
        [
            new CreateItemLinkRequest(DocumentKind, manual.Id.ToString()),
            new CreateItemLinkRequest(TaskKind, Guid.NewGuid().ToString())
        ]), HestiaApiFactory.Json), "targetId");
        Assert.Equal(0, await host.WithDbAsync(db => db.Items.CountAsync()));

        var item = await host.CreateAsync(new CreateItemRequest("Oven", Links:
        [
            new CreateItemLinkRequest(DocumentKind, manual.Id.ToString()),
            new CreateItemLinkRequest(DocumentKind, manual.Id.ToString().ToUpperInvariant())
        ]));
        Assert.Equal(manual.Id.ToString(), Assert.Single(item.Links).TargetId);
    }

    [Fact]
    public async Task TheItem_ShowsItsDocumentsAndTasks_FromBothSidesOfTheLink()
    {
        var (host, finance, _) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var item = await host.CreateAsync("Furnace");
        var manual = await host.UploadDocumentAsync("Furnace manual");
        var warranty = await host.UploadDocumentAsync("Furnace warranty", new DateOnly(2030, 1, 1));
        // The item links to the manual; the warranty links to the item (Documents, kind Home.Item).
        await host.Client.PostAsJsonAsync($"{Items}/{item.Id}/links", new CreateItemLinkRequest(DocumentKind, manual.Id.ToString()));
        var fromDocument = await host.Client.PostAsJsonAsync($"/api/documents/{warranty.Id}/links",
            new CreateDocumentLinkRequest(HomeItem, item.Id.ToString()));
        Assert.Equal(HttpStatusCode.Created, fromDocument.StatusCode);
        // A repeated task links to the item (Tasks, kind Home.Item); the item links to a task.
        var filter = await CreateTaskAsync(host, new CreateTaskRequest("Change the furnace filter", DueOn: HomeHost.Today.AddDays(20),
            Repeat: new TaskRepeatRequest(3, RepeatUnit.Months, RepeatMode.FromDueDate),
            Links: [new CreateTaskLinkRequest(HomeItem, item.Id.ToString())]));
        var service = await CreateTaskAsync(host, new CreateTaskRequest("Book the yearly service", DueOn: HomeHost.Today.AddDays(5)));
        await host.Client.PostAsJsonAsync($"{Items}/{item.Id}/links", new CreateItemLinkRequest(TaskKind, service.Id.ToString()));
        await CreateTaskAsync(host, new CreateTaskRequest("Not linked"));

        var after = await host.GetAsync(item.Id);

        Assert.Equal(new[]
        {
            new ItemDocumentDto(manual.Id, "Furnace manual", "Other", null),
            new ItemDocumentDto(warranty.Id, "Furnace warranty", "Other", new DateOnly(2030, 1, 1))
        }, after.Documents);
        Assert.Equal(new[]
        {
            new ItemTaskDto(service.Id, "Book the yearly service", "Open", HomeHost.Today.AddDays(5), null, null, null),
            new ItemTaskDto(filter.Id, "Change the furnace filter", "Open", HomeHost.Today.AddDays(20), 3, "Months", null)
        }, after.Maintenance);

        // by-link finds the item from both sides too.
        async Task<Guid[]> ByLink(string kind, Guid id) =>
            (await (await host.Client.GetAsync($"{Items}/by-link?kind={kind}&id={id}")).ReadAsync<List<ItemSummaryDto>>()).Select(i => i.Id).ToArray();
        Assert.Equal(new[] { item.Id }, await ByLink(DocumentKind, manual.Id));
        Assert.Equal(new[] { item.Id }, await ByLink(DocumentKind, warranty.Id));
        Assert.Equal(new[] { item.Id }, await ByLink(TaskKind, filter.Id));
        Assert.Equal(new[] { item.Id }, await ByLink(TaskKind, service.Id));
        Assert.Empty(await ByLink(DocumentKind, Guid.NewGuid()));
        await HomeHost.AssertBadRequestAsync(await host.Client.GetAsync($"{Items}/by-link?kind=People.Person&id=1"), "kind");

        // The query interface of other modules reads the Home links only.
        var queried = await host.Factory.WithScopeAsync(services =>
            services.GetRequiredService<IHomeQueries>().ListByLinkAsync(DocumentKind, manual.Id.ToString()));
        Assert.Equal(new[] { item.Id }, queried.Select(i => i.Id));
        Assert.Empty(await host.Factory.WithScopeAsync(services =>
            services.GetRequiredService<IHomeQueries>().ListByLinkAsync(DocumentKind, warranty.Id.ToString())));
        Assert.Empty(await host.Factory.WithScopeAsync(services => services.GetRequiredService<IHomeQueries>().ListByLinkAsync("People.Person", "1")));

        var link = after.Links.First(l => l.Kind == DocumentKind);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{Items}/{item.Id}/links/{link.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"{Items}/{item.Id}/links/{link.Id}")).StatusCode);
        Assert.Equal(new[] { warranty.Id }, (await host.GetAsync(item.Id)).Documents.Select(d => d.Id));
    }

    [Fact]
    public async Task DocumentsAndTasks_CheckTheirHomeItemLinks()
    {
        var (host, finance, _) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var document = await host.UploadDocumentAsync("Receipt");
        var task = await CreateTaskAsync(host, new CreateTaskRequest("Clean the filter"));

        await HomeHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync($"/api/documents/{document.Id}/links",
            new CreateDocumentLinkRequest(HomeItem, Guid.NewGuid().ToString())), "targetId");
        await HomeHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync($"/api/documents/{document.Id}/links",
            new CreateDocumentLinkRequest(HomeItem, "1")), "targetId");
        await HomeHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/links",
            new CreateTaskLinkRequest(HomeItem, Guid.NewGuid().ToString())), "targetId");
        Assert.Empty(finance.Requests);
    }

    [Fact]
    public async Task ADeletedItem_LosesTheLinksOfDocumentsAndTasks_AndTheyStay()
    {
        var (host, finance, _) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var item = await host.CreateAsync("Washer");
        var other = await host.CreateAsync("Dryer");
        var document = await host.UploadDocumentAsync("Washer manual");
        await host.Client.PostAsJsonAsync($"/api/documents/{document.Id}/links", new CreateDocumentLinkRequest(HomeItem, item.Id.ToString()));
        await host.Client.PostAsJsonAsync($"/api/documents/{document.Id}/links", new CreateDocumentLinkRequest(HomeItem, other.Id.ToString()));
        var task = await CreateTaskAsync(host, new CreateTaskRequest("Clean the washer drum",
            Links: [new CreateTaskLinkRequest(HomeItem, item.Id.ToString())]));

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{Items}/{item.Id}")).StatusCode);

        var unlinked = await host.Factory.Events.WaitForAsync<DocumentUnlinked>(e => e.DocumentId == document.Id);
        Assert.Equal((HomeItem, item.Id.ToString()), (unlinked.LinkKind, unlinked.TargetId));
        var changed = await host.Factory.Events.WaitForAsync<TaskChanged>(e => e.TaskId == task.Id && e.Change == TaskChange.Links);
        Assert.Equal("Clean the washer drum", changed.Title);
        var documentAfter = await (await host.Client.GetAsync($"/api/documents/{document.Id}")).ReadAsync<DocumentDto>();
        Assert.Equal(new[] { other.Id.ToString() }, documentAfter.Links.Select(l => l.TargetId));
        var taskAfter = await (await host.Client.GetAsync($"/api/tasks/{task.Id}")).ReadAsync<TaskDto>();
        Assert.Empty(taskAfter.Links);
    }

    [Fact]
    public async Task ADeletedDocumentOrTask_LosesItsItemLinks_AndTheItemStays()
    {
        var (host, finance, _) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var document = await host.UploadDocumentAsync("Receipt");
        var task = await CreateTaskAsync(host, new CreateTaskRequest("Descale"));
        var item = await host.CreateAsync(new CreateItemRequest("Kettle", Links:
        [
            new CreateItemLinkRequest(DocumentKind, document.Id.ToString()),
            new CreateItemLinkRequest(TaskKind, task.Id.ToString())
        ]));

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"/api/documents/{document.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"/api/tasks/{task.Id}")).StatusCode);

        var after = await Wait.UntilAsync(() => host.GetAsync(item.Id), i => i.Links.Count == 0);
        Assert.Empty(after.Links);
        Assert.Equal("Kettle", after.Name);
        await host.Factory.Events.WaitForAsync<ItemChanged>(e => e.ItemId == item.Id && e.Change == ItemChange.Links);
        Assert.Equal(2, await host.WithDbAsync(db => db.Set<InboxEntry>()
            .CountAsync(entry => entry.Consumer == ItemLinkCleanupConsumer.InboxName)));
    }

    [Fact]
    public async Task TheSameDeleteEvent_Twice_ChangesTheLinksOnce()
    {
        var (host, finance, _) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var item = await host.CreateAsync("Kettle");
        var documentId = Guid.NewGuid();
        var deleted = new DocumentDeleted(documentId, "Receipt", HomeHost.Now);

        await host.DeliverAsync(deleted);
        // A new link to the same document id (for example the document came back from a backup) must stay: the second
        // delivery of the same event changes nothing.
        await host.WithDbAsync(async db =>
        {
            db.Links.Add(new Domain.ItemLink
            {
                Id = Guid.CreateVersion7(), ItemId = item.Id, Kind = DocumentKind, TargetId = documentId.ToString(), CreatedAt = HomeHost.Now
            });
            return await db.SaveChangesAsync();
        });
        await host.DeliverAsync(deleted);

        Assert.Single((await host.WithDbAsync(db => db.Links.Where(l => l.ItemId == item.Id).ToListAsync())));
        Assert.Equal(1, await host.WithDbAsync(db => db.Set<InboxEntry>()
            .CountAsync(entry => entry.EventId == deleted.EventId && entry.Consumer == ItemLinkCleanupConsumer.InboxName)));
    }

    [Fact]
    public async Task TheSameItemDeleted_Twice_ChangesTheDocumentAndTaskLinksOnce()
    {
        var (host, finance, _) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var item = await host.CreateAsync("Kettle");
        var document = await host.UploadDocumentAsync("Receipt");
        var task = await CreateTaskAsync(host, new CreateTaskRequest("Descale"));
        var deleted = new ItemDeleted(item.Id, "Kettle", HomeHost.Now);

        await host.DeliverAsync(deleted);
        // Links made after the first delivery (for example the item came back from a backup) must stay.
        await host.Client.PostAsJsonAsync($"/api/documents/{document.Id}/links", new CreateDocumentLinkRequest(HomeItem, item.Id.ToString()));
        await host.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/links", new CreateTaskLinkRequest(HomeItem, item.Id.ToString()));
        await host.DeliverAsync(deleted);

        Assert.Single((await (await host.Client.GetAsync($"/api/documents/{document.Id}")).ReadAsync<DocumentDto>()).Links);
        Assert.Single((await (await host.Client.GetAsync($"/api/tasks/{task.Id}")).ReadAsync<TaskDto>()).Links);
    }
}
