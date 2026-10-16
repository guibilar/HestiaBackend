using System.Net.Http.Json;
using Hestia.Modules.Calendar.Contracts;
using Hestia.Modules.Journal.Contracts;
using Hestia.Modules.People.Contracts;
using Hestia.Modules.Tasks.Contracts;
using Hestia.Modules.Today.Contracts;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hestia.Modules.Today.Tests;

/// <summary>
/// T106: Today has "Your day" (the Calendar items of today and of the next week) and "On this day" (the journal entries
/// of this day in earlier years).
/// </summary>
public sealed class TodayAgendaTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateOnly Day = FinanceData.Today;

    private static async Task<HestiaApiFactory> HostAsync(FakeFinance finance) =>
        await Task.FromResult(new HestiaApiFactory(
            finance.HostSettings(new Dictionary<string, string?> { ["Hestia:TimeZone"] = FinanceData.TimeZone }),
            services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTime(FinanceData.Now));
            }));

    private static async Task PostAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, HestiaApiFactory.Json);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Today_HasYourDay_TheNextDays_AndOnThisDay()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = await HostAsync(finance);
        var client = factory.CreateClient();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(FinanceData.TimeZone);
        DateTimeOffset At(DateOnly day, int hour) =>
            new(day.ToDateTime(new TimeOnly(hour, 0)), zone.GetUtcOffset(day.ToDateTime(new TimeOnly(hour, 0))));

        await PostAsync(client, "/api/calendar/events", new CalendarEventRequest("Dinner with Ana", StartsAt: At(Day, 19), Location: "Luigi's"));
        await PostAsync(client, "/api/calendar/events", new CalendarEventRequest("Dentist", StartsAt: At(Day.AddDays(2), 9)));
        await PostAsync(client, "/api/tasks", new CreateTaskRequest("Water the plants", DueOn: Day));
        await PostAsync(client, "/api/people", new CreatePersonRequest("Bruno", Birthday: new BirthdayDto(Day.AddDays(3).Month, Day.AddDays(3).Day)));
        // Beyond the week: not in "Upcoming".
        await PostAsync(client, "/api/tasks", new CreateTaskRequest("Clean the gutters", DueOn: Day.AddDays(12)));
        var put = await client.PutAsJsonAsync($"/api/journal/entries/{Day.AddYears(-1):yyyy-MM-dd}",
            new SaveJournalEntryRequest("Moved into the new place."), HestiaApiFactory.Json);
        Assert.True(put.IsSuccessStatusCode);

        var today = await (await client.GetAsync("/api/today?refresh=true")).ReadAsync<TodayDto>();

        var agenda = Assert.IsType<TodayAgendaDto>(today.Agenda);
        Assert.True(agenda.Complete);
        // The whole-day task first, then the event by time.
        Assert.Equal(["Water the plants", "Dinner with Ana"], agenda.Today.Select(i => i.Title));
        Assert.Equal(("Tasks", "task-due", false), (agenda.Today[0].Module, agenda.Today[0].Kind, agenda.Today[0].Done!.Value));
        Assert.Equal(("Luigi's", At(Day, 19)), (agenda.Today[1].Text, agenda.Today[1].StartsAt!.Value));
        Assert.NotNull(agenda.Today[1].EventId);
        Assert.Equal(["Dentist", "Bruno's birthday"], agenda.Upcoming.Select(i => i.Title));

        var memory = Assert.Single(today.Memories!);
        Assert.Equal(("Journal", 1, Day.AddYears(-1), "Moved into the new place."), (memory.Module, memory.YearsAgo, memory.Day, memory.Text));
        Assert.Equal($"/journal?day={Day.AddYears(-1):yyyy-MM-dd}", memory.Link);
    }

    [Fact]
    public async Task AnEmptyDay_HasAnEmptyAgenda_AndNoMemories()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = await HostAsync(finance);

        var today = await (await factory.CreateClient().GetAsync("/api/today")).ReadAsync<TodayDto>();

        Assert.Equal((0, 0), (today.Agenda!.Today.Count, today.Agenda.Upcoming.Count));
        Assert.Empty(today.Memories!);
    }
}
