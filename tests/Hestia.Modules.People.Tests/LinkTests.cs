using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hestia.Modules.Documents.Contracts;
using Hestia.Modules.Home.Contracts;
using Hestia.Modules.People.Contracts;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Hestia.Modules.People.Tests;

/// <summary>
/// Links from a person to a document, a task, a home item, or a finance transaction (T20), and the clean-up when the
/// linked thing is deleted.
/// </summary>
public class LinkTests
{
    private const string People = PeopleHost.People;

    /// <summary>Finance's transaction filter: it finds the transactions in <see cref="Known"/>.</summary>
    private sealed class Transactions
    {
        public HashSet<Guid> Known { get; } = [];
        public FakeFinance Finance { get; set; } = null!;

        public async Task RespondAsync(HttpContext context)
        {
            if (context.Request.Path == "/health/ready")
            {
                await context.Response.WriteAsync("Healthy");
                return;
            }
            var body = Finance.Requests.Last(request => request.Target == "/api/Transaction/Filter").Body;
            var id = JsonDocument.Parse(body).RootElement.GetProperty("transactionId").GetGuid();
            await context.Response.WriteAsJsonAsync(new { items = Array.Empty<object>(), page = 1, pageSize = 1, totalCount = Known.Contains(id) ? 1 : 0 });
        }
    }

    private static async Task<T> CreateAsync<T>(PeopleHost host, string url, object body)
    {
        var response = await host.Client.PostAsJsonAsync(url, body, HestiaApiFactory.Json);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<T>();
    }

    [Fact]
    public async Task Links_ToADocument_ATask_AHomeItem_AndATransaction_AreChecked_AndShown()
    {
        var transactions = new Transactions();
        await using var finance = await FakeFinance.StartAsync(transactions.RespondAsync);
        transactions.Finance = finance;
        await using var host = PeopleHost.Start(finance.HostSettings());
        var transactionId = Guid.NewGuid();
        transactions.Known.Add(transactionId);
        var document = await host.UploadDocumentAsync("Marco's card");
        var task = await CreateAsync<TaskDto>(host, "/api/tasks", new CreateTaskRequest("Call Marco"));
        var item = await CreateAsync<ItemDto>(host, "/api/home/items", new CreateItemRequest("Camping stove"));

        var person = await host.CreateAsync(new CreatePersonRequest("Marco",
            Links: [new CreatePersonLinkRequest("documents.document", document.Id.ToString())]));
        await CreateAsync<PersonLinkDto>(host, $"{People}/{person.Person.Id}/links", new CreatePersonLinkRequest(PersonLinkKinds.TasksTask, task.Id.ToString()));
        await CreateAsync<PersonLinkDto>(host, $"{People}/{person.Person.Id}/links", new CreatePersonLinkRequest(PersonLinkKinds.HomeItem, item.Id.ToString()));
        await CreateAsync<PersonLinkDto>(host, $"{People}/{person.Person.Id}/links", new CreatePersonLinkRequest(PersonLinkKinds.MoneyTransaction, transactionId.ToString()));

        var read = await host.GetAsync(person.Person.Id);
        Assert.Equal([PersonLinkKinds.DocumentsDocument, PersonLinkKinds.TasksTask, PersonLinkKinds.HomeItem, PersonLinkKinds.MoneyTransaction],
            read.Links.Select(l => l.Kind));
        Assert.Equal(TestSignIn.UserId, GatewayToken.SubjectOf(Assert.Single(finance.Requests, r => r.Target == "/api/Transaction/Filter")));

        var byLink = await (await host.Client.GetAsync($"{People}/by-link?kind=Home.Item&id={item.Id}")).ReadAsync<List<PersonSummaryDto>>();
        Assert.Equal([person.Person.Id], byLink.Select(p => p.Id));

        Assert.Equal(HttpStatusCode.Conflict,
            (await host.Client.PostAsJsonAsync($"{People}/{person.Person.Id}/links", new CreatePersonLinkRequest(PersonLinkKinds.HomeItem, item.Id.ToString()))).StatusCode);
        await PeopleHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync($"{People}/{person.Person.Id}/links",
            new CreatePersonLinkRequest(PersonLinkKinds.HomeItem, Guid.NewGuid().ToString())), "targetId");
        await PeopleHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync($"{People}/{person.Person.Id}/links",
            new CreatePersonLinkRequest(PersonLinkKinds.MoneyTransaction, Guid.NewGuid().ToString())), "targetId");
        await PeopleHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync($"{People}/{person.Person.Id}/links",
            new CreatePersonLinkRequest("Journal.Entry", Guid.NewGuid().ToString())), "kind");

        var link = read.Links[0];
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{People}/{person.Person.Id}/links/{link.Id}")).StatusCode);
        Assert.Equal(3, (await host.GetAsync(person.Person.Id)).Links.Count);
    }

    [Fact]
    public async Task ADeletedDocument_Task_OrHomeItem_LosesItsLinks_Once()
    {
        await using var host = PeopleHost.Start();
        var document = await host.UploadDocumentAsync("Card");
        var task = await CreateAsync<TaskDto>(host, "/api/tasks", new CreateTaskRequest("Call"));
        var item = await CreateAsync<ItemDto>(host, "/api/home/items", new CreateItemRequest("Stove"));
        var person = await host.CreateAsync(new CreatePersonRequest("Marco", Links:
        [
            new CreatePersonLinkRequest(PersonLinkKinds.DocumentsDocument, document.Id.ToString()),
            new CreatePersonLinkRequest(PersonLinkKinds.TasksTask, task.Id.ToString()),
            new CreatePersonLinkRequest(PersonLinkKinds.HomeItem, item.Id.ToString())
        ]));

        await host.Client.DeleteAsync($"/api/documents/{document.Id}");
        await host.Client.DeleteAsync($"/api/tasks/{task.Id}");
        await host.Client.DeleteAsync($"/api/home/items/{item.Id}");

        var read = await Wait.UntilAsync(() => host.GetAsync(person.Person.Id), p => p.Links.Count == 0);
        Assert.Empty(read.Links);
        await host.Factory.Events.WaitForAsync<PersonChanged>(e => e.PersonId == person.Person.Id && e.Change == PersonChange.Links);

        // The same event again changes nothing (the inbox).
        var again = new ItemDeleted(item.Id, "Stove", PeopleHost.Now);
        await host.DeliverAsync(again);
        await host.DeliverAsync(again);
        var inbox = await host.WithDbAsync(db => db.Set<Hestia.Platform.Events.InboxEntry>()
            .CountAsync(row => row.EventId == again.EventId));
        Assert.Equal(1, inbox);
    }
}
