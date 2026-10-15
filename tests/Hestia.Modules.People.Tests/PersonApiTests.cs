using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.People.Contracts;
using Hestia.Testing;

namespace Hestia.Modules.People.Tests;

/// <summary>The people endpoints (T20): add, read, list (groups, search, sort), change, delete, and the access rules.</summary>
public class PersonApiTests
{
    private const string People = PeopleHost.People;
    private static readonly DateOnly Today = PeopleHost.Today;

    [Fact]
    public async Task APerson_IsAdded_WithCleanValues_AndRead()
    {
        await using var host = PeopleHost.Start();

        var person = await host.CreateAsync(new CreatePersonRequest("  Marco   Silva ", "Marc", "Friend", ["Friends, Gaming  Group", "friends"],
            new BirthdayDto(11, 2, 1990), "marco@example.com", "+1 403 555 0100", "  Likes coffee.  ", 42));

        var read = await host.GetAsync(person.Person.Id);
        var summary = read.Person;
        Assert.Equal(("Marco Silva", "Marc", "Friend"), (summary.Name, summary.Nickname, summary.Relationship));
        Assert.Equal(["friends", "gaming group"], summary.Groups);
        Assert.Equal(new BirthdayDto(11, 2, 1990), summary.Birthday);
        Assert.Equal(new DateOnly(2026, 11, 2), summary.NextBirthday);
        Assert.Equal(("marco@example.com", "+1 403 555 0100", "Likes coffee."), (summary.Email, summary.Phone, read.Notes));
        Assert.Equal(42, summary.ContactEveryDays);
        // No contact yet: the reminder counts from the day the person was added.
        Assert.Null(summary.LastContactOn);
        Assert.Equal(Today.AddDays(42), summary.ContactDueOn);
        Assert.False(summary.ContactOverdue);
        Assert.Empty(read.Interactions);
        Assert.Empty(read.GiftIdeas);
        Assert.Empty(read.Loans);
    }

    [Fact]
    public async Task WrongValues_AreBadRequests()
    {
        await using var host = PeopleHost.Start();

        await PeopleHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(People, new CreatePersonRequest("  ")), "name");
        await PeopleHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(People, new CreatePersonRequest("A", Birthday: new BirthdayDto(2, 30))), "birthday");
        await PeopleHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(People, new CreatePersonRequest("A", Birthday: new BirthdayDto(13, 1))), "birthday");
        await PeopleHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(People, new CreatePersonRequest("A", Birthday: new BirthdayDto(2, 29, 2023))), "birthday");
        await PeopleHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(People, new CreatePersonRequest("A", Birthday: new BirthdayDto(12, 1, 2026))), "birthday");
        await PeopleHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(People, new CreatePersonRequest("A", ContactEveryDays: 0)), "contactEveryDays");
        await PeopleHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(People, new CreatePersonRequest("A", Email: "not an email")), "email");
        await PeopleHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync(People, new CreatePersonRequest("A", Groups: ["a|b"])), "groups");
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"{People}/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task February29_IsFebruary28_InAYearWithoutIt()
    {
        await using var host = PeopleHost.Start(now: new DateTimeOffset(2027, 1, 10, 16, 0, 0, TimeSpan.Zero));

        var person = await host.CreateAsync("Leap", birthday: new BirthdayDto(2, 29, 2000));

        Assert.Equal(new DateOnly(2027, 2, 28), person.Person.NextBirthday);
    }

    [Fact]
    public async Task TheList_FiltersByGroup_Searches_AndSortsByName_OrByTheReachOutDay()
    {
        await using var host = PeopleHost.Start();
        var priya = await host.CreateAsync("Priya Rao", 30, null, "Friends");
        var ana = await host.CreateAsync("Ana Lima", 7, null, "Family");
        var daniel = await host.CreateAsync(new CreatePersonRequest("Daniel Costa", Relationship: "Brother", Groups: ["Family"], Notes: "Plays guitar"));
        await host.LogContactAsync(priya.Person.Id, Today.AddDays(-40));

        var all = await host.ListAsync();
        Assert.Equal(["Ana Lima", "Daniel Costa", "Priya Rao"], all.Items.Select(p => p.Name));
        Assert.Equal(3, all.TotalCount);

        Assert.Equal(["Ana Lima", "Daniel Costa"], (await host.ListAsync("group=FAMILY")).Items.Select(p => p.Name));
        Assert.Equal(["Daniel Costa"], (await host.ListAsync("q=guitar")).Items.Select(p => p.Name));
        Assert.Equal(["Daniel Costa"], (await host.ListAsync("q=brother costa")).Items.Select(p => p.Name));

        // Priya: due 30 days after the contact 40 days ago (overdue); Ana: 7 days after today's add; Daniel: no reminder.
        var byContact = await host.ListAsync("sort=contact");
        Assert.Equal([priya.Person.Id, ana.Person.Id, daniel.Person.Id], byContact.Items.Select(p => p.Id));
        Assert.True(byContact.Items[0].ContactOverdue);
        Assert.Equal(["Priya Rao"], (await host.ListAsync("sort=contact&pageSize=1")).Items.Select(p => p.Name));

        await PeopleHost.AssertBadRequestAsync(await host.Client.GetAsync($"{People}?sort=age"), "sort");

        var groups = await (await host.Client.GetAsync($"{People}/groups")).ReadAsync<List<GroupCountDto>>();
        Assert.Equal([new GroupCountDto("family", 2), new GroupCountDto("friends", 1)], groups);
    }

    [Fact]
    public async Task AChange_ReplacesTheDetails_AndADelete_RemovesEverything()
    {
        await using var host = PeopleHost.Start();
        var person = await host.CreateAsync("Marco", 42, new BirthdayDto(11, 2), "Friends");
        await host.LogContactAsync(person.Person.Id);
        await host.PostAsync<GiftIdeaDto>($"{People}/{person.Person.Id}/gifts", new GiftIdeaRequest("Kettle"));
        await host.AddLoanAsync(person.Person.Id, new LoanRequest("Camping stove"));

        var changed = await host.PutAsync<PersonDto>($"{People}/{person.Person.Id}", new UpdatePersonRequest("Marco Silva", Groups: ["Gaming group"]));

        Assert.Equal("Marco Silva", changed.Person.Name);
        Assert.Equal(["gaming group"], changed.Person.Groups);
        Assert.Null(changed.Person.Birthday);
        Assert.Null(changed.Person.ContactEveryDays);
        // The records stay.
        Assert.Single(changed.Interactions);
        Assert.Equal(Today, changed.Person.LastContactOn);

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{People}/{person.Person.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"{People}/{person.Person.Id}")).StatusCode);
        Assert.Equal(0, await host.WithDbAsync(db => Task.FromResult(db.Interactions.Count() + db.Gifts.Count() + db.Loans.Count())));
    }

    [Fact]
    public async Task WithoutSignIn_EveryEndpointIs401()
    {
        await using var host = PeopleHost.Start(signedIn: false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(People)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync($"{People}/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsJsonAsync(People, new CreatePersonRequest("A"))).StatusCode);
    }

    [Fact]
    public async Task AReadOnlyUser_CanRead_ButNotWrite()
    {
        await using var host = PeopleHost.Start();
        var person = await host.CreateAsync("Marco");
        var readOnly = host.Factory.CreateClient();
        readOnly.DefaultRequestHeaders.Add(TestSignIn.ReadOnlyHeader, "yes");

        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync(People)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"{People}/{person.Person.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync($"{People}/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync(People, new CreatePersonRequest("A"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await readOnly.PostAsJsonAsync($"{People}/{person.Person.Id}/interactions", new InteractionRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.DeleteAsync($"{People}/{person.Person.Id}")).StatusCode);
    }
}
