using System.Net.Http.Json;
using System.Text.Json;
using Hestia.Modules.People.Contracts;
using Hestia.Modules.People.Infrastructure;
using Hestia.Platform.Events;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.People.Tests;

/// <summary>
/// The People events are durable from the first migration (T38): each event is a row of <c>people.EventOutbox</c>, stored
/// with the change, and the host's sender delivers it, also after a restart.
/// </summary>
public class DurableEventTests
{
    private const string People = PeopleHost.People;

    [Fact]
    public async Task EachEvent_ReachesItsConsumers_AndLeavesTheOutbox()
    {
        await using var host = PeopleHost.Start();

        var person = (await host.CreateAsync("Marco", 42, null, "Friends")).Person;
        var contact = await host.LogContactAsync(person.Id, kind: "Call");
        var gift = await host.PostAsync<GiftIdeaDto>($"{People}/{person.Id}/gifts", new GiftIdeaRequest("Kettle", Price: 50m));
        var loan = await host.AddLoanAsync(person.Id, new LoanRequest("Stove"));
        await host.PutAsync<PersonDto>($"{People}/{person.Id}", new UpdatePersonRequest("Marco Silva"));
        await host.Client.DeleteAsync($"{People}/{person.Id}");

        var added = await host.Factory.Events.WaitForAsync<PersonAdded>(e => e.PersonId == person.Id);
        Assert.Equal(["friends"], added.Groups);
        var logged = await host.Factory.Events.WaitForAsync<InteractionChanged>(e => e.InteractionId == contact.Id);
        Assert.Equal((PeopleRecordChange.Added, "Call", PeopleHost.Today), (logged.Change, logged.Kind, logged.OccurredOn));
        var giftEvent = await host.Factory.Events.WaitForAsync<GiftChanged>(e => e.GiftId == gift.Id);
        Assert.Equal((PeopleRecordChange.Added, 50m), (giftEvent.Change, giftEvent.Price));
        await host.Factory.Events.WaitForAsync<LoanChanged>(e => e.LoanId == loan.Id && e.Change == PeopleRecordChange.Added);
        var changed = await host.Factory.Events.WaitForAsync<PersonChanged>(e => e.PersonId == person.Id);
        Assert.Equal((PersonChange.Details, "Marco Silva"), (changed.Change, changed.Name));
        await host.Factory.Events.WaitForAsync<PersonDeleted>(e => e.PersonId == person.Id);
        await Wait.UntilAsync(() => OutboxAsync(host.Factory), rows => rows.Count == 0);
        Assert.Empty(await OutboxAsync(host.Factory));
    }

    [Fact]
    public async Task PersonAdded_IsStoredInTheOutbox_WithThePerson_AndSurvivesARestart()
    {
        var clock = new TestClock(PeopleHost.Now);
        var first = new HestiaApiFactory(PeopleHost.Settings(new Dictionary<string, string?> { ["Events:Outbox:Enabled"] = "false" }),
            PeopleHost.Services(clock));
        var response = await first.CreateClient().PostAsJsonAsync(People, new CreatePersonRequest("Ana"), HestiaApiFactory.Json);
        var person = await response.ReadAsync<PersonDto>();

        var row = Assert.Single(await OutboxAsync(first));
        Assert.Equal(typeof(PersonAdded).FullName, row.EventType);
        var stored = JsonSerializer.Deserialize<PersonAdded>(row.Payload)!;
        Assert.Equal(person.Person.Id, stored.PersonId);
        Assert.Empty(await first.Events.AfterQuietPeriodAsync<PersonAdded>());

        await using var second = first.Restarted(PeopleHost.Settings(), PeopleHost.Services(clock));
        await first.DisposeAsync();
        second.CreateClient();

        var received = await second.Events.WaitForAsync<PersonAdded>(e => e.PersonId == person.Person.Id);
        Assert.Equal(stored.EventId, received.EventId);
        await Wait.UntilAsync(() => OutboxAsync(second), rows => rows.Count == 0);
    }

    private static Task<List<OutboxMessage>> OutboxAsync(HestiaApiFactory factory) =>
        factory.WithScopeAsync(services =>
            services.GetRequiredService<PeopleDbContext>().Set<OutboxMessage>().AsNoTracking().OrderBy(row => row.Id).ToListAsync());
}
