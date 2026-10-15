using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.People.Contracts;
using Hestia.Platform.Paging;
using Hestia.Testing;

namespace Hestia.Modules.People.Tests;

/// <summary>The records of a person (T20): contacts (they move the last contact), gift ideas, loans, and the summary.</summary>
public class RecordTests
{
    private const string People = PeopleHost.People;
    private static readonly DateOnly Today = PeopleHost.Today;

    [Fact]
    public async Task Contacts_MoveTheLastContact_WhenTheyAreAdded_Changed_AndDeleted()
    {
        await using var host = PeopleHost.Start();
        var person = (await host.CreateAsync("Marco", 42)).Person;

        var coffee = await host.LogContactAsync(person.Id, Today.AddDays(-47), " Coffee ", "Talked about his new job.");
        var call = await host.LogContactAsync(person.Id, Today.AddDays(-50), "Call");
        Assert.Equal(("Coffee", "Talked about his new job."), (coffee.Kind, coffee.Note));

        var read = await host.GetAsync(person.Id);
        Assert.Equal(Today.AddDays(-47), read.Person.LastContactOn);
        Assert.Equal(Today.AddDays(-5), read.Person.ContactDueOn);
        Assert.True(read.Person.ContactOverdue);
        Assert.Equal([coffee.Id, call.Id], read.Interactions.Select(i => i.Id));
        Assert.Equal(2, read.InteractionCount);

        // Today's contact (no day: today) ends the reminder.
        var today = await host.LogContactAsync(person.Id);
        Assert.Equal(Today, today.OccurredOn);
        Assert.False((await host.GetAsync(person.Id)).Person.ContactOverdue);

        await host.PutAsync<InteractionDto>($"{People}/interactions/{today.Id}", new InteractionRequest(Today.AddDays(-60), "Call"));
        Assert.Equal(Today.AddDays(-47), (await host.GetAsync(person.Id)).Person.LastContactOn);

        await host.Client.DeleteAsync($"{People}/interactions/{coffee.Id}");
        await host.Client.DeleteAsync($"{People}/interactions/{call.Id}");
        Assert.Equal(Today.AddDays(-60), (await host.GetAsync(person.Id)).Person.LastContactOn);
        await host.Client.DeleteAsync($"{People}/interactions/{today.Id}");
        Assert.Null((await host.GetAsync(person.Id)).Person.LastContactOn);

        await PeopleHost.AssertBadRequestAsync(
            await host.Client.PostAsJsonAsync($"{People}/{person.Id}/interactions", new InteractionRequest(Today.AddDays(1)), HestiaApiFactory.Json),
            "occurredOn");
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.Client.PostAsJsonAsync($"{People}/{Guid.NewGuid()}/interactions", new InteractionRequest(), HestiaApiFactory.Json)).StatusCode);
    }

    [Fact]
    public async Task TheContactList_IsPaged_NewestFirst()
    {
        await using var host = PeopleHost.Start();
        var person = (await host.CreateAsync("Marco")).Person;
        for (var i = 0; i < 25; i++)
        {
            await host.LogContactAsync(person.Id, Today.AddDays(-i));
        }

        Assert.Equal(20, (await host.GetAsync(person.Id)).Interactions.Count);
        var page = await (await host.Client.GetAsync($"{People}/{person.Id}/interactions?page=2&pageSize=10")).ReadAsync<PaginatedList<InteractionDto>>();
        Assert.Equal(25, page.TotalCount);
        Assert.Equal(Enumerable.Range(10, 10).Select(i => Today.AddDays(-i)), page.Items.Select(i => i.OccurredOn));
    }

    [Fact]
    public async Task GiftIdeas_AreIdeasUntilGiven_IdeasFirst()
    {
        await using var host = PeopleHost.Start();
        var person = (await host.CreateAsync("Marco")).Person;
        var kettle = await host.PostAsync<GiftIdeaDto>($"{People}/{person.Id}/gifts", new GiftIdeaRequest("Pour-over kettle", Price: 49.999m));
        var pack = await host.PostAsync<GiftIdeaDto>($"{People}/{person.Id}/gifts", new GiftIdeaRequest("Hiking day pack"));
        var game = await host.PostAsync<GiftIdeaDto>($"{People}/{person.Id}/gifts",
            new GiftIdeaRequest("Board game", Price: 64m, Occasion: "Christmas", GivenOn: new DateOnly(2025, 12, 25)));
        Assert.Equal(50.00m, kettle.Price);

        Assert.Equal([kettle.Id, pack.Id, game.Id], (await host.GetAsync(person.Id)).GiftIdeas.Select(g => g.Id));

        var given = await host.PutAsync<GiftIdeaDto>($"{People}/gifts/{kettle.Id}", new GiftIdeaRequest("Pour-over kettle", Price: 50m, GivenOn: Today));
        Assert.Equal(Today, given.GivenOn);
        Assert.Equal([pack.Id, kettle.Id, game.Id], (await host.GetAsync(person.Id)).GiftIdeas.Select(g => g.Id));

        await PeopleHost.AssertBadRequestAsync(
            await host.Client.PostAsJsonAsync($"{People}/{person.Id}/gifts", new GiftIdeaRequest(""), HestiaApiFactory.Json), "title");
        await PeopleHost.AssertBadRequestAsync(
            await host.Client.PostAsJsonAsync($"{People}/{person.Id}/gifts", new GiftIdeaRequest("A", Price: -1), HestiaApiFactory.Json), "price");
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{People}/gifts/{pack.Id}")).StatusCode);
        Assert.Equal(2, (await host.GetAsync(person.Id)).GiftIdeas.Count);
    }

    [Fact]
    public async Task Loans_AreOpenUntilReturned_AndOverdueAfterTheDueDay()
    {
        await using var host = PeopleHost.Start();
        var marco = (await host.CreateAsync("Marco")).Person;
        var ana = (await host.CreateAsync("Ana")).Person;
        var stove = await host.AddLoanAsync(marco.Id, new LoanRequest("Camping stove", LentOn: new DateOnly(2026, 7, 4), DueOn: new DateOnly(2026, 8, 1)));
        var book = await host.AddLoanAsync(ana.Id, new LoanRequest("Novel", LoanDirection.Borrowed, DueOn: Today.AddDays(10)));
        var tent = await host.AddLoanAsync(marco.Id, new LoanRequest("Tent", LentOn: Today.AddDays(-30), ReturnedOn: Today.AddDays(-2)));

        Assert.Equal((LoanDirection.Lent, true, "Marco"), (stove.Direction, stove.Overdue, stove.PersonName));
        Assert.Equal((LoanDirection.Borrowed, false, Today), (book.Direction, book.Overdue, book.LentOn));

        var open = await (await host.Client.GetAsync($"{People}/loans")).ReadAsync<List<LoanDto>>();
        Assert.Equal([stove.Id, book.Id], open.Select(l => l.Id));
        Assert.Equal([tent.Id], (await (await host.Client.GetAsync($"{People}/loans?status=returned")).ReadAsync<List<LoanDto>>()).Select(l => l.Id));
        Assert.Equal(3, (await (await host.Client.GetAsync($"{People}/loans?status=all")).ReadAsync<List<LoanDto>>()).Count);
        Assert.Equal([stove.Id, tent.Id], (await host.GetAsync(marco.Id)).Loans.Select(l => l.Id));

        var back = await host.PutAsync<LoanDto>($"{People}/loans/{stove.Id}",
            new LoanRequest("Camping stove", LentOn: new DateOnly(2026, 7, 4), DueOn: new DateOnly(2026, 8, 1), ReturnedOn: Today));
        Assert.False(back.Overdue);

        await PeopleHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync($"{People}/{marco.Id}/loans",
            new LoanRequest("A", DueOn: Today.AddDays(-1)), HestiaApiFactory.Json), "dueOn");
        await PeopleHost.AssertBadRequestAsync(await host.Client.PostAsJsonAsync($"{People}/{marco.Id}/loans",
            new LoanRequest("A", ReturnedOn: Today.AddDays(1)), HestiaApiFactory.Json), "returnedOn");
        await PeopleHost.AssertBadRequestAsync(await host.Client.GetAsync($"{People}/loans?status=lost"), "status");
    }

    [Fact]
    public async Task TheSummary_ListsReachOut_UpcomingBirthdays_AndLoans()
    {
        await using var host = PeopleHost.Start();
        var marco = (await host.CreateAsync("Marco Silva", 42, new BirthdayDto(11, 2, 1990))).Person;
        var ana = (await host.CreateAsync("Ana Lima", 60)).Person;
        await host.CreateAsync("Priya", null, new BirthdayDto(12, 25));
        await host.CreateAsync("Daniel", null, new BirthdayDto(10, 14));
        await host.LogContactAsync(marco.Id, Today.AddDays(-45));
        await host.LogContactAsync(ana.Id, Today.AddDays(-90));
        await host.PostAsync<GiftIdeaDto>($"{People}/{marco.Id}/gifts", new GiftIdeaRequest("Kettle"));
        await host.PostAsync<GiftIdeaDto>($"{People}/{marco.Id}/gifts", new GiftIdeaRequest("Pack"));
        await host.PostAsync<GiftIdeaDto>($"{People}/{marco.Id}/gifts", new GiftIdeaRequest("Game", GivenOn: Today.AddDays(-300)));
        await host.AddLoanAsync(marco.Id, new LoanRequest("Stove", LentOn: Today.AddDays(-60), DueOn: Today.AddDays(-30)));
        await host.AddLoanAsync(ana.Id, new LoanRequest("Novel", LoanDirection.Borrowed));

        var summary = await host.SummaryAsync();

        Assert.Equal((Today, 4), (summary.Date, summary.PeopleCount));
        // Ana is 30 days overdue, Marco 3 days.
        Assert.Equal(["Ana Lima", "Marco Silva"], summary.ReachOut.Select(p => p.Name));
        Assert.Equal(
            [new UpcomingBirthdayDto(summary.Upcoming[0].PersonId, "Daniel", Today, 0, null, 0),
             new UpcomingBirthdayDto(marco.Id, "Marco Silva", new DateOnly(2026, 11, 2), 19, 36, 2)],
            summary.Upcoming);
        Assert.Equal((2, 1), (summary.OpenLoans, summary.OverdueLoans));
    }
}
