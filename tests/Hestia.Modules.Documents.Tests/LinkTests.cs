using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.People.Contracts;
using Hestia.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Documents.Tests;

/// <summary>
/// Links from a document to a finance transaction. The host checks the transaction with finance (<see cref="FakeFinance"/>
/// here), as the signed-in user, and never reads the finance database.
/// </summary>
public class LinkTests
{
    private const string Documents = DocumentsHost.Documents;
    private const string Kind = DocumentLinkKinds.MoneyTransaction;

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
            // The fake read the body already; it is in the recorded request.
            var body = Finance.Requests.Last(request => request.Target == "/api/Transaction/Filter").Body;
            var id = JsonDocument.Parse(body).RootElement.GetProperty("transactionId").GetGuid();
            await context.Response.WriteAsJsonAsync(new { items = Array.Empty<object>(), page = 1, pageSize = 1, totalCount = Known.Contains(id) ? 1 : 0 });
        }
    }

    private static async Task<(DocumentsHost Host, FakeFinance Finance, Transactions Transactions)> StartAsync()
    {
        var transactions = new Transactions();
        var finance = await FakeFinance.StartAsync(transactions.RespondAsync);
        transactions.Finance = finance;
        return (DocumentsHost.Start(finance.HostSettings()), finance, transactions);
    }

    [Fact]
    public async Task ALink_ToATransactionThatExists_IsStored_AndRaisesDocumentLinked()
    {
        var (host, finance, transactions) = await StartAsync();
        await using var _ = host;
        await using var __ = finance;
        var transactionId = Guid.NewGuid();
        transactions.Known.Add(transactionId);
        var document = await host.UploadOkAsync();

        var response = await host.Client.PostAsJsonAsync($"{Documents}/{document.Id}/links",
            new CreateDocumentLinkRequest("money.transaction", transactionId.ToString("D").ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var link = await response.ReadAsync<DocumentLinkDto>();
        Assert.Equal((Kind, transactionId.ToString("D")), (link.Kind, link.TargetId));
        Assert.Equal($"{Documents}/{document.Id}/links/{link.Id}", response.Headers.Location?.OriginalString);

        // Finance got the filter for this one transaction, with the gateway token of the signed-in user.
        var request = Assert.Single(finance.Requests);
        Assert.Equal(("POST", "/api/Transaction/Filter"), (request.Method, request.Target));
        var filter = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal((transactionId, 1, 1), (filter.GetProperty("transactionId").GetGuid(), filter.GetProperty("page").GetInt32(),
            filter.GetProperty("pageSize").GetInt32()));
        var token = GatewayToken.Read(request);
        Assert.Equal((TestSignIn.UserId, "full"), (token.Subject, token.Scope));

        var linked = await host.Factory.Events.WaitForAsync<DocumentLinked>();
        Assert.Equal((document.Id, link.Id, Kind, link.TargetId), (linked.DocumentId, linked.LinkId, linked.LinkKind, linked.TargetId));
        var stored = await (await host.Client.GetAsync($"{Documents}/{document.Id}")).ReadAsync<DocumentDto>();
        var storedLink = Assert.Single(stored.Links);
        Assert.Equal((link.Id, link.Kind, link.TargetId), (storedLink.Id, storedLink.Kind, storedLink.TargetId));
    }

    [Fact]
    public async Task ByLink_ListsTheDocumentsOfATransaction()
    {
        var (host, finance, transactions) = await StartAsync();
        await using var _ = host;
        await using var __ = finance;
        var transactionId = Guid.NewGuid();
        transactions.Known.Add(transactionId);
        var receipt = await host.UploadOkAsync("Receipt");
        var warranty = await host.UploadOkAsync("Warranty", "Warranty", files: [SampleFile.Pdf]);
        await host.UploadOkAsync("Not linked");
        foreach (var document in new[] { receipt, warranty })
        {
            await host.Client.PostAsJsonAsync($"{Documents}/{document.Id}/links", new CreateDocumentLinkRequest(Kind, transactionId.ToString()));
        }

        var found = await (await host.Client.GetAsync($"{Documents}/by-link?kind={Kind}&id={transactionId}"))
            .ReadAsync<List<DocumentSummaryDto>>();

        Assert.Equal(new[] { warranty.Id, receipt.Id }, found.Select(d => d.Id));
        Assert.Empty(await (await host.Client.GetAsync($"{Documents}/by-link?kind={Kind}&id={Guid.NewGuid()}"))
            .ReadAsync<List<DocumentSummaryDto>>());
        await UploadTests.AssertBadRequestAsync(await host.Client.GetAsync($"{Documents}/by-link?kind=Journal.Entry&id=1"), "kind");

        // Other modules use the query interface.
        var queried = await host.Factory.WithScopeAsync(services =>
            services.GetRequiredService<IDocumentQueries>().ListByLinkAsync(Kind, transactionId.ToString()));
        Assert.Equal(new[] { warranty.Id, receipt.Id }, queried.Select(d => d.Id));
        Assert.Empty(await host.Factory.WithScopeAsync(services =>
            services.GetRequiredService<IDocumentQueries>().ListByLinkAsync("People.Person", "1")));
        var one = await host.Factory.WithScopeAsync(services => services.GetRequiredService<IDocumentQueries>().FindAsync(receipt.Id));
        Assert.Equal(transactionId.ToString(), Assert.Single(one!.Links).TargetId);
    }

    [Fact]
    public async Task ALink_ThatCannotBeMade_IsABadRequest_OrNotFound_OrAConflict()
    {
        var (host, finance, transactions) = await StartAsync();
        await using var _ = host;
        await using var __ = finance;
        var transactionId = Guid.NewGuid();
        transactions.Known.Add(transactionId);
        var document = await host.UploadOkAsync();
        var links = $"{Documents}/{document.Id}/links";

        await UploadTests.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateDocumentLinkRequest(Kind, Guid.NewGuid().ToString())), "targetId");
        await UploadTests.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateDocumentLinkRequest(Kind, "not-a-guid")), "targetId");
        await UploadTests.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateDocumentLinkRequest("Journal.Entry", "1")), "kind");
        await UploadTests.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links, new CreateDocumentLinkRequest(null, "1")), "kind");
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsJsonAsync($"{Documents}/{Guid.NewGuid()}/links",
            new CreateDocumentLinkRequest(Kind, transactionId.ToString()))).StatusCode);
        // Only the first request asked finance: the others failed before.
        Assert.Single(finance.Requests);

        Assert.Equal(HttpStatusCode.Created, (await host.Client.PostAsJsonAsync(links, new CreateDocumentLinkRequest(Kind, transactionId.ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsJsonAsync(links, new CreateDocumentLinkRequest(Kind, transactionId.ToString()))).StatusCode);
    }

    [Fact]
    public async Task Counts_GivesTheDocumentsOfEachTransaction_ZeroIncluded()
    {
        var (host, finance, transactions) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var linked = Guid.NewGuid();
        transactions.Known.Add(linked);
        var plain = Guid.NewGuid();
        var receipt = await host.UploadOkAsync("Receipt", "Receipt");
        var warranty = await host.UploadOkAsync("Warranty", "Warranty");
        foreach (var document in new[] { receipt, warranty })
        {
            Assert.Equal(HttpStatusCode.Created, (await host.Client.PostAsJsonAsync($"{Documents}/{document.Id}/links",
                new CreateDocumentLinkRequest(Kind, linked.ToString()))).StatusCode);
        }

        var response = await host.Client.PostAsJsonAsync($"{Documents}/by-link/counts",
            new DocumentLinkCountsRequest(Kind, [linked.ToString().ToUpperInvariant(), plain.ToString()]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var counts = await response.ReadAsync<Dictionary<string, int>>();
        Assert.Equal(new Dictionary<string, int> { [linked.ToString("D")] = 2, [plain.ToString("D")] = 0 }, counts);
        await UploadTests.AssertBadRequestAsync(await host.Client.PostAsJsonAsync($"{Documents}/by-link/counts",
            new DocumentLinkCountsRequest("Journal.Entry", [])), "kind");
        await UploadTests.AssertBadRequestAsync(await host.Client.PostAsJsonAsync($"{Documents}/by-link/counts",
            new DocumentLinkCountsRequest(Kind, Enumerable.Range(0, 201).Select(_ => Guid.NewGuid().ToString()).ToList())), "targetIds");
    }

    [Fact]
    public async Task FinanceDown_TheLinkIs502_AndNothingIsStored()
    {
        var (host, finance, transactions) = await StartAsync();
        await using var _ = host;
        await using var __ = finance;
        transactions.Status = StatusCodes.Status503ServiceUnavailable;
        var document = await host.UploadOkAsync();

        var response = await host.Client.PostAsJsonAsync($"{Documents}/{document.Id}/links", new CreateDocumentLinkRequest(Kind, Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty((await (await host.Client.GetAsync($"{Documents}/{document.Id}")).ReadAsync<DocumentDto>()).Links);
    }

    [Fact]
    public async Task RemoveLink_DeletesIt_AndRaisesDocumentUnlinked()
    {
        var (host, finance, transactions) = await StartAsync();
        await using var _ = host;
        await using var __ = finance;
        var transactionId = Guid.NewGuid();
        transactions.Known.Add(transactionId);
        var document = await host.UploadOkAsync();
        var link = await (await host.Client.PostAsJsonAsync($"{Documents}/{document.Id}/links",
            new CreateDocumentLinkRequest(Kind, transactionId.ToString()))).ReadAsync<DocumentLinkDto>();

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{Documents}/{document.Id}/links/{link.Id}")).StatusCode);

        var unlinked = await host.Factory.Events.WaitForAsync<DocumentUnlinked>();
        Assert.Equal((document.Id, link.Id, transactionId.ToString()), (unlinked.DocumentId, unlinked.LinkId, unlinked.TargetId));
        Assert.Empty(await (await host.Client.GetAsync($"{Documents}/by-link?kind={Kind}&id={transactionId}")).ReadAsync<List<DocumentSummaryDto>>());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"{Documents}/{document.Id}/links/{link.Id}")).StatusCode);
    }

    [Fact]
    public async Task DeletingADocument_UnlinksItFirst_ThenRaisesDocumentDeleted()
    {
        var (host, finance, transactions) = await StartAsync();
        await using var _ = host;
        await using var __ = finance;
        var transactionIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        transactions.Known.UnionWith(transactionIds);
        var document = await host.UploadOkAsync();
        foreach (var id in transactionIds)
        {
            await host.Client.PostAsJsonAsync($"{Documents}/{document.Id}/links", new CreateDocumentLinkRequest(Kind, id.ToString()));
        }

        await host.Client.DeleteAsync($"{Documents}/{document.Id}");

        await host.Factory.Events.WaitForAsync<DocumentDeleted>();
        var unlinked = await Wait.UntilAsync(
            () => Task.FromResult(host.Factory.Events.Events.OfType<DocumentUnlinked>().ToList()), found => found.Count == 2);
        Assert.Equal(transactionIds.Select(id => id.ToString()).Order(), unlinked.Select(e => e.TargetId).Order());
        Assert.Empty(await (await host.Client.GetAsync($"{Documents}/by-link?kind={Kind}&id={transactionIds[0]}")).ReadAsync<List<DocumentSummaryDto>>());
    }

    [Fact]
    public async Task ALink_ToAHomeItem_IsCheckedWithTheHomeModule_AndGoesWhenTheItemIsDeleted()
    {
        var (host, finance, _) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var document = await host.UploadOkAsync("Dishwasher manual", "Manual");
        var created = await host.Client.PostAsJsonAsync("/api/home/items", new CreateItemRequest("Dishwasher"), HestiaApiFactory.Json);
        var item = await created.ReadAsync<ItemDto>();
        var links = $"{Documents}/{document.Id}/links";

        await UploadTests.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links,
            new CreateDocumentLinkRequest(DocumentLinkKinds.HomeItem, Guid.NewGuid().ToString())), "targetId");
        var response = await host.Client.PostAsJsonAsync(links, new CreateDocumentLinkRequest("home.item", item.Id.ToString().ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var link = await response.ReadAsync<DocumentLinkDto>();
        Assert.Equal((DocumentLinkKinds.HomeItem, item.Id.ToString("D")), (link.Kind, link.TargetId));
        Assert.Empty(finance.Requests);
        var found = await (await host.Client.GetAsync($"{Documents}/by-link?kind=Home.Item&id={item.Id}")).ReadAsync<List<DocumentSummaryDto>>();
        Assert.Equal(new[] { document.Id }, found.Select(d => d.Id));

        // The item is deleted: the consumer removes the link (with the inbox), and the document stays.
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"/api/home/items/{item.Id}")).StatusCode);
        var unlinked = await host.Factory.Events.WaitForAsync<DocumentUnlinked>(e => e.LinkId == link.Id);
        Assert.Equal((document.Id, DocumentLinkKinds.HomeItem), (unlinked.DocumentId, unlinked.LinkKind));
        Assert.Empty((await (await host.Client.GetAsync($"{Documents}/{document.Id}")).ReadAsync<DocumentDto>()).Links);
        Assert.Equal(1, await host.WithDbAsync(db => db.Set<Hestia.Platform.Events.InboxEntry>()
            .CountAsync(entry => entry.Consumer == Application.DocumentItemLinksConsumer.InboxName)));
    }

    [Fact]
    public async Task ALink_ToAPerson_IsCheckedWithThePeopleModule_ShowsOnThePerson_AndGoesWhenThePersonIsDeleted()
    {
        var (host, finance, _) = await StartAsync();
        await using var _host = host;
        await using var _finance = finance;
        var document = await host.UploadOkAsync("Prescription", "Other");
        var created = await host.Client.PostAsJsonAsync("/api/people", new CreatePersonRequest("Dr. Ana Costa"), HestiaApiFactory.Json);
        var person = (await created.ReadAsync<PersonDto>()).Person;
        var links = $"{Documents}/{document.Id}/links";

        await UploadTests.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(links,
            new CreateDocumentLinkRequest(DocumentLinkKinds.PeoplePerson, Guid.NewGuid().ToString())), "targetId");
        var response = await host.Client.PostAsJsonAsync(links, new CreateDocumentLinkRequest("people.person", person.Id.ToString()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var link = await response.ReadAsync<DocumentLinkDto>();
        Assert.Equal((DocumentLinkKinds.PeoplePerson, person.Id.ToString("D")), (link.Kind, link.TargetId));
        Assert.Empty(finance.Requests);

        // The person page lists the document (the link belongs to the document), and the people of the document include
        // the person.
        var page = await (await host.Client.GetAsync($"/api/people/{person.Id}")).ReadAsync<PersonDto>();
        Assert.Empty(page.Links);
        Assert.Equal(new PersonLinkedFromDto(PersonLinkKinds.DocumentsDocument, document.Id.ToString("D"), "Prescription"), Assert.Single(page.LinkedFrom));
        var people = await (await host.Client.GetAsync($"/api/people/by-link?kind=Documents.Document&id={document.Id}")).ReadAsync<List<PersonSummaryDto>>();
        Assert.Equal(new[] { person.Id }, people.Select(p => p.Id));

        // The person is deleted: the consumer removes the link (with the inbox), and the document stays.
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"/api/people/{person.Id}")).StatusCode);
        var unlinked = await host.Factory.Events.WaitForAsync<DocumentUnlinked>(e => e.LinkId == link.Id);
        Assert.Equal((document.Id, DocumentLinkKinds.PeoplePerson), (unlinked.DocumentId, unlinked.LinkKind));
        Assert.Empty((await (await host.Client.GetAsync($"{Documents}/{document.Id}")).ReadAsync<DocumentDto>()).Links);
        Assert.Equal(1, await host.WithDbAsync(db => db.Set<Hestia.Platform.Events.InboxEntry>()
            .CountAsync(entry => entry.Consumer == Application.DocumentPersonLinksConsumer.InboxName)));
    }
}
