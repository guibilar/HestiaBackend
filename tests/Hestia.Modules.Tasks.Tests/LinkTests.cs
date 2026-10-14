using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.Tasks.Application;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Platform.Events;
using Hestia.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Hestia.Modules.Tasks.Tests;

/// <summary>
/// Links from a task to a document or a finance transaction (T19). The host checks a document with the Documents module
/// (<see cref="IDocumentQueries"/>) and a transaction with finance (<see cref="FakeFinance"/> here), as the signed-in user.
/// </summary>
public class LinkTests
{
    private const string Tasks = TasksHost.Tasks;
    private const string DocumentKind = TaskLinkKinds.DocumentsDocument;
    private const string MoneyKind = TaskLinkKinds.MoneyTransaction;

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

    private static async Task<(TasksHost Host, FakeFinance Finance, Transactions Transactions)> StartAsync()
    {
        var transactions = new Transactions();
        var finance = await FakeFinance.StartAsync(transactions.RespondAsync);
        transactions.Finance = finance;
        return (TasksHost.Start(finance.HostSettings()), finance, transactions);
    }

    /// <summary>Uploads a small document (a PNG header is enough: the OCR is off) and returns its id.</summary>
    private static async Task<Guid> UploadDocumentAsync(TasksHost host, string title)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(title), "title");
        // New content for each document (the bytes after the header differ).
        var file = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Guid.NewGuid().ToByteArray()]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "files", "policy.png");
        var response = await host.Client.PostAsync("/api/documents", form);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.ReadAsync<DocumentDto>()).Id;
    }

    [Fact]
    public async Task ALink_ToADocument_IsStored_AndRaisesTaskChanged()
    {
        var (host, finance, _) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var documentId = await UploadDocumentAsync(host, "Home insurance policy");
        var task = await host.CreateAsync("Renew home insurance", TasksHost.Today.AddDays(18));

        var response = await host.Client.PostAsJsonAsync($"{Tasks}/{task.Id}/links",
            new CreateTaskLinkRequest("documents.document", documentId.ToString("D").ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var link = await response.ReadAsync<TaskLinkDto>();
        Assert.Equal((DocumentKind, documentId.ToString("D")), (link.Kind, link.TargetId));
        Assert.Equal($"{Tasks}/{task.Id}/links/{link.Id}", response.Headers.Location?.OriginalString);
        Assert.Empty(finance.Requests);
        var changed = await host.Factory.Events.WaitForAsync<TaskChanged>(e => e.Change == TaskChange.Links);
        Assert.Equal(task.Id, changed.TaskId);
        Assert.Equal(link.Id, Assert.Single((await host.GetAsync(task.Id)).Links).Id);

        // The same link again is a conflict.
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsJsonAsync($"{Tasks}/{task.Id}/links",
            new CreateTaskLinkRequest(DocumentKind, documentId.ToString()))).StatusCode);
    }

    [Fact]
    public async Task ALink_ToATransaction_IsCheckedWithFinance_AsTheSignedInUser()
    {
        var (host, finance, transactions) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var transactionId = Guid.NewGuid();
        transactions.Known.Add(transactionId);
        var task = await host.CreateAsync("Ask for a refund");

        var response = await host.Client.PostAsJsonAsync($"{Tasks}/{task.Id}/links", new CreateTaskLinkRequest(MoneyKind, transactionId.ToString()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var request = Assert.Single(finance.Requests);
        Assert.Equal(("POST", "/api/Transaction/Filter"), (request.Method, request.Target));
        var filter = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal((transactionId, 1, 1), (filter.GetProperty("transactionId").GetGuid(), filter.GetProperty("page").GetInt32(),
            filter.GetProperty("pageSize").GetInt32()));
        var token = GatewayToken.SubjectOf(request);
        Assert.Equal(TestSignIn.UserId, token);
    }

    [Fact]
    public async Task ALink_ThatCannotBeMade_IsABadRequest_OrNotFound()
    {
        var (host, finance, _) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var task = await host.CreateAsync("Task");
        var links = $"{Tasks}/{task.Id}/links";

        await TasksHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateTaskLinkRequest(DocumentKind, Guid.NewGuid().ToString())), "targetId");
        await TasksHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateTaskLinkRequest(MoneyKind, Guid.NewGuid().ToString())), "targetId");
        await TasksHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateTaskLinkRequest(DocumentKind, "not-a-guid")), "targetId");
        await TasksHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateTaskLinkRequest("People.Person", "1")), "kind");
        await TasksHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateTaskLinkRequest(null, "1")), "kind");
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsJsonAsync($"{Tasks}/{Guid.NewGuid()}/links",
            new CreateTaskLinkRequest(DocumentKind, Guid.NewGuid().ToString()))).StatusCode);
        Assert.Empty((await host.GetAsync(task.Id)).Links);
    }

    [Fact]
    public async Task FinanceDown_TheLinkIs502_AndNothingIsStored()
    {
        var (host, finance, transactions) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        transactions.Status = StatusCodes.Status503ServiceUnavailable;
        var task = await host.CreateAsync("Task");

        var response = await host.Client.PostAsJsonAsync($"{Tasks}/{task.Id}/links", new CreateTaskLinkRequest(MoneyKind, Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty((await host.GetAsync(task.Id)).Links);
    }

    [Fact]
    public async Task Create_WithLinks_ChecksThemAll_BeforeItStoresTheTask()
    {
        var (host, finance, transactions) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var documentId = await UploadDocumentAsync(host, "Warranty");
        var transactionId = Guid.NewGuid();
        transactions.Known.Add(transactionId);

        var bad = await host.Client.PostAsJsonAsync(Tasks, new CreateTaskRequest("Claim the warranty", Links:
        [
            new CreateTaskLinkRequest(DocumentKind, documentId.ToString()),
            new CreateTaskLinkRequest(MoneyKind, Guid.NewGuid().ToString())
        ]), HestiaApiFactory.Json);
        await TasksHost.AssertBadRequestAsync(bad, "targetId");
        Assert.Equal(0, await host.WithDbAsync(db => db.Tasks.CountAsync()));

        var task = await host.CreateAsync(new CreateTaskRequest("Claim the warranty", Links:
        [
            new CreateTaskLinkRequest(DocumentKind, documentId.ToString()),
            new CreateTaskLinkRequest(MoneyKind, transactionId.ToString()),
            new CreateTaskLinkRequest(DocumentKind, documentId.ToString().ToUpperInvariant())
        ]));

        Assert.Equal(new[] { (DocumentKind, documentId.ToString()), (MoneyKind, transactionId.ToString()) }.Order(),
            task.Links.Select(l => (l.Kind, l.TargetId)).Order());
    }

    [Fact]
    public async Task ByLink_ListsTheTasksOfAThing_AndRemoveLink_DeletesIt()
    {
        var (host, finance, _) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var documentId = await UploadDocumentAsync(host, "Lease");
        var renew = await host.CreateAsync(new CreateTaskRequest("Renew the lease", DueOn: TasksHost.Today.AddDays(30),
            Links: [new CreateTaskLinkRequest(DocumentKind, documentId.ToString())]));
        var read = await host.CreateAsync(new CreateTaskRequest("Read the lease",
            Links: [new CreateTaskLinkRequest(DocumentKind, documentId.ToString())]));
        await host.CreateAsync("Not linked", TasksHost.Today);

        var found = await (await host.Client.GetAsync($"{Tasks}/by-link?kind={DocumentKind}&id={documentId}")).ReadAsync<List<TaskSummaryDto>>();

        Assert.Equal(new[] { renew.Id, read.Id }, found.Select(t => t.Id));
        await TasksHost.AssertBadRequestAsync(await host.Client.GetAsync($"{Tasks}/by-link?kind=People.Person&id=1"), "kind");
        var queried = await host.Factory.WithScopeAsync(services =>
            services.GetRequiredService<ITaskQueries>().ListByLinkAsync(DocumentKind, documentId.ToString()));
        Assert.Equal(new[] { renew.Id, read.Id }, queried.Select(t => t.Id));
        Assert.Empty(await host.Factory.WithScopeAsync(services => services.GetRequiredService<ITaskQueries>().ListByLinkAsync("People.Person", "1")));

        var link = Assert.Single(renew.Links);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{Tasks}/{renew.Id}/links/{link.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"{Tasks}/{renew.Id}/links/{link.Id}")).StatusCode);
        var after = await (await host.Client.GetAsync($"{Tasks}/by-link?kind={DocumentKind}&id={documentId}")).ReadAsync<List<TaskSummaryDto>>();
        Assert.Equal(new[] { read.Id }, after.Select(t => t.Id));
    }

    [Fact]
    public async Task ADeletedDocument_LosesItsTaskLinks_AndTheTasksStay()
    {
        var (host, finance, transactions) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var documentId = await UploadDocumentAsync(host, "Car insurance policy");
        var transactionId = Guid.NewGuid();
        transactions.Known.Add(transactionId);
        var task = await host.CreateAsync(new CreateTaskRequest("Renew car insurance", Links:
        [
            new CreateTaskLinkRequest(DocumentKind, documentId.ToString()),
            new CreateTaskLinkRequest(MoneyKind, transactionId.ToString())
        ]));

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"/api/documents/{documentId}")).StatusCode);

        var changed = await host.Factory.Events.WaitForAsync<TaskChanged>(e => e.TaskId == task.Id && e.Change == TaskChange.Links);
        Assert.Equal("Renew car insurance", changed.Title);
        var after = await host.GetAsync(task.Id);
        Assert.Equal(new[] { MoneyKind }, after.Links.Select(l => l.Kind));
        Assert.Equal(1, await host.WithDbAsync(db => db.Set<InboxEntry>()
            .CountAsync(entry => entry.Consumer == DocumentDeletedConsumer.InboxName)));
    }

    [Fact]
    public async Task TheSameDocumentDeleted_Twice_ChangesTheLinksOnce()
    {
        var (host, finance, _) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var documentId = await UploadDocumentAsync(host, "Manual");
        var task = await host.CreateAsync(new CreateTaskRequest("Read the manual",
            Links: [new CreateTaskLinkRequest(DocumentKind, documentId.ToString())]));
        var deleted = new DocumentDeleted(documentId, "Manual", TasksHost.Now);

        await DeliverAsync(host, deleted);
        // A new link to the same document id (for example the document came back from a backup) must stay: the second
        // delivery of the same event changes nothing.
        await host.WithDbAsync(async db =>
        {
            db.Links.Add(new Domain.TaskLink
            {
                Id = Guid.CreateVersion7(), TaskId = task.Id, Kind = DocumentKind, TargetId = documentId.ToString(), CreatedAt = TasksHost.Now
            });
            return await db.SaveChangesAsync();
        });
        await DeliverAsync(host, deleted);

        Assert.Single((await host.GetAsync(task.Id)).Links);
        Assert.Equal(1, await host.WithDbAsync(db => db.Set<InboxEntry>()
            .CountAsync(entry => entry.EventId == deleted.EventId && entry.Consumer == DocumentDeletedConsumer.InboxName)));
    }

    [Fact]
    public async Task ALink_ToAHomeItem_IsCheckedWithTheHomeModule_AndGoesWhenTheItemIsDeleted()
    {
        var (host, finance, _) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var created = await host.Client.PostAsJsonAsync("/api/home/items", new CreateItemRequest("Furnace"), HestiaApiFactory.Json);
        var item = await created.ReadAsync<ItemDto>();
        var task = await host.CreateAsync("Change the furnace filter", TasksHost.Today.AddDays(10), TasksHost.Every(3, RepeatUnit.Months));
        var links = $"{Tasks}/{task.Id}/links";

        await TasksHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links,
            new CreateTaskLinkRequest(TaskLinkKinds.HomeItem, Guid.NewGuid().ToString())), "targetId");
        await TasksHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateTaskLinkRequest(TaskLinkKinds.HomeItem, "1")), "targetId");
        var response = await host.Client.PostAsJsonAsync(links, new CreateTaskLinkRequest("home.item", item.Id.ToString()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty(finance.Requests);
        var found = await (await host.Client.GetAsync($"{Tasks}/by-link?kind=Home.Item&id={item.Id}")).ReadAsync<List<TaskSummaryDto>>();
        Assert.Equal(new[] { task.Id }, found.Select(t => t.Id));

        // The item is deleted: the consumer removes the link (with the inbox), and the task stays.
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"/api/home/items/{item.Id}")).StatusCode);
        await Wait.UntilAsync(() => host.GetAsync(task.Id), t => t.Links.Count == 0);
        Assert.Empty((await host.GetAsync(task.Id)).Links);
        Assert.Equal(1, await host.WithDbAsync(db => db.Set<InboxEntry>()
            .CountAsync(entry => entry.Consumer == TaskItemLinksConsumer.InboxName)));
    }

    private static async Task DeliverAsync(TasksHost host, object message)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message);
    }
}
