using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.Calendar.Contracts;
using Hestia.Testing;

namespace Hestia.Modules.Calendar.Tests;

/// <summary>T103: events of the Calendar: add, change, repeat, skip, delete, and the view of a range.</summary>
public class CalendarEventTests
{
    private static readonly DateOnly Today = CalendarHost.Today;

    [Fact]
    public async Task AnEventWithATime_IsAdded_Read_Changed_AndDeleted()
    {
        await using var host = CalendarHost.Start();
        var start = CalendarHost.Toronto(2026, 10, 20, 18, 30);

        var created = await host.CreateAsync(new CalendarEventRequest("  Dentist  ", StartsAt: start, Location: "Main St 4"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var added = await created.ReadAsync<CalendarEventDto>();
        Assert.Equal(("Dentist", false, start, start.AddHours(1), CalendarHost.TimeZone, CalendarEventSource.Manual),
            (added.Title, added.AllDay, added.StartsAt, added.EndsAt, added.TimeZone, added.Source));

        var changed = await host.Client.PutAsJsonAsync($"{CalendarHost.Calendar}/events/{added.Id}",
            new CalendarEventRequest("Dentist (cleaning)", StartsAt: start, EndsAt: start.AddMinutes(45)), HestiaApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var read = await (await host.Client.GetAsync($"{CalendarHost.Calendar}/events/{added.Id}")).ReadAsync<CalendarEventDto>();
        // PUT replaces all values: the place is gone.
        Assert.Equal(("Dentist (cleaning)", start.AddMinutes(45), (string?)null), (read.Title, read.EndsAt, read.Location));

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{CalendarHost.Calendar}/events/{added.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"{CalendarHost.Calendar}/events/{added.Id}")).StatusCode);
    }

    [Theory]
    [InlineData("""{"title":" "}""", "title")]
    [InlineData("""{"title":"Trip","allDay":true}""", "startDate")]
    [InlineData("""{"title":"Trip","allDay":true,"startDate":"2026-10-20","endDate":"2026-10-19"}""", "endDate")]
    [InlineData("""{"title":"Call"}""", "startsAt")]
    [InlineData("""{"title":"Call","startsAt":"2026-10-20T10:00:00Z","endsAt":"2026-10-20T09:00:00Z"}""", "endsAt")]
    [InlineData("""{"title":"Call","startsAt":"2026-10-20T10:00:00Z","timeZone":"Mars/Olympus"}""", "timeZone")]
    [InlineData("""{"title":"Call","startsAt":"2026-10-20T10:00:00Z","repeat":"EVERY TUESDAY"}""", "repeat")]
    [InlineData("""{"title":"Call","startsAt":"2026-10-20T10:00:00Z","repeat":"FREQ=HOURLY"}""", "repeat")]
    public async Task ABadEvent_IsRefused_WithTheField(string json, string field)
    {
        await using var host = CalendarHost.Start();

        var response = await host.Client.PostAsync($"{CalendarHost.Calendar}/events",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AWeeklyEvent_KeepsItsLocalTime_OverTheEndOfSummerTime_AndSkipsADay()
    {
        await using var host = CalendarHost.Start();
        var standup = await host.AddAsync(new CalendarEventRequest("Standup", StartsAt: CalendarHost.Toronto(2026, 10, 20, 9),
            EndsAt: CalendarHost.Toronto(2026, 10, 20, 9, 15), Repeat: "RRULE:FREQ=WEEKLY;BYDAY=TU"));
        Assert.Equal("FREQ=WEEKLY;BYDAY=TU", standup.Repeat);

        var view = await host.ViewAsync(Today, new DateOnly(2026, 11, 10));
        var standups = view.Items.Where(i => i.EventId == standup.Id).ToList();
        // 09:00 in Toronto: 13:00 UTC in summer time, 14:00 UTC after November 1st.
        Assert.Equal(
            [
                new DateTimeOffset(2026, 10, 20, 13, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 27, 13, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 3, 14, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero)
            ],
            standups.Select(i => i.StartsAt!.Value));
        Assert.All(standups, i => Assert.True(i.Recurring));
        Assert.Equal(standups.Count, standups.Select(i => i.Id).Distinct().Count());
        Assert.Equal($"calendar.event.{standup.Id}.20261103T0900", standups[2].Id);

        var skip = await host.Client.PostAsJsonAsync($"{CalendarHost.Calendar}/events/{standup.Id}/skip",
            new CalendarSkipRequest(new DateOnly(2026, 10, 27)), HestiaApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);
        Assert.Equal([new DateOnly(2026, 10, 27)], (await skip.ReadAsync<CalendarEventDto>()).SkippedDates);
        var after = await host.ViewAsync(Today, new DateOnly(2026, 11, 10));
        Assert.Equal([new DateOnly(2026, 10, 20), new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 10)],
            after.Items.Where(i => i.EventId == standup.Id).Select(i => i.StartDate));
    }

    [Fact]
    public async Task ASingleEvent_CannotSkipADay()
    {
        await using var host = CalendarHost.Start();
        var single = await host.AddAsync(new CalendarEventRequest("Call", StartsAt: CalendarHost.Toronto(2026, 10, 20, 9)));

        var skip = await host.Client.PostAsJsonAsync($"{CalendarHost.Calendar}/events/{single.Id}/skip",
            new CalendarSkipRequest(new DateOnly(2026, 10, 20)), HestiaApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, skip.StatusCode);
    }

    [Fact]
    public async Task AllDayEvents_AreOnEachOfTheirDays_AndComeBeforeTheTimes_AndAYearlyOneRepeats()
    {
        await using var host = CalendarHost.Start();
        // A trip that starts before the view and ends in it.
        var trip = await host.AddAsync(new CalendarEventRequest("Trip", AllDay: true, StartDate: Today.AddDays(-2), EndDate: Today.AddDays(1)));
        var lunch = await host.AddAsync(new CalendarEventRequest("Lunch", StartsAt: CalendarHost.Toronto(2026, 10, 16, 12)));
        var anniversary = await host.AddAsync(new CalendarEventRequest("Anniversary", AllDay: true, StartDate: new DateOnly(2020, 10, 18),
            Repeat: "FREQ=YEARLY"));
        // Before the view: not in it.
        await host.AddAsync(new CalendarEventRequest("Old", StartsAt: CalendarHost.Toronto(2026, 10, 15, 23), EndsAt: CalendarHost.Toronto(2026, 10, 16, 0)));

        var view = await host.ViewAsync(Today, Today.AddDays(7));

        var mine = view.Items.Where(i => i.Module == "Calendar").ToList();
        Assert.Equal(["Trip", "Lunch", "Anniversary"], mine.Select(i => i.Title));
        Assert.Equal((Today.AddDays(-2), Today.AddDays(1)), (mine[0].StartDate, mine[0].EndDate));
        Assert.Equal(new DateOnly(2026, 10, 18), mine[2].StartDate);
        Assert.Equal($"/calendar?event={trip.Id}", mine[0].Link);
        Assert.Equal((lunch.Id, anniversary.Id), (mine[1].EventId!.Value, mine[2].EventId!.Value));
        Assert.Equal(CalendarHost.TimeZone, view.TimeZone);
        Assert.Equal(Today, view.Today);
    }

    [Fact]
    public async Task TheView_HasLimits_AndDefaultsToTheNext31Days()
    {
        await using var host = CalendarHost.Start();

        var view = await (await host.Client.GetAsync(CalendarHost.Calendar)).ReadAsync<CalendarDto>();
        Assert.Equal((Today, Today.AddDays(30)), (view.From, view.To));
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync($"{CalendarHost.Calendar}?from=2026-10-16&to=2026-10-15")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync($"{CalendarHost.Calendar}?from=2026-01-01&to=2027-12-31")).StatusCode);
    }

    [Fact]
    public async Task AReadOnlyUser_ReadsOnly_AndAnUnknownUserGets401()
    {
        await using var host = CalendarHost.Start();
        using var readOnly = host.Factory.CreateClient();
        readOnly.DefaultRequestHeaders.Add(TestSignIn.ReadOnlyHeader, "yes");

        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync(CalendarHost.Calendar)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync($"{CalendarHost.Calendar}/events",
            new CalendarEventRequest("Call", StartsAt: CalendarHost.Now), HestiaApiFactory.Json)).StatusCode);

        await using var anonymous = CalendarHost.Start(signedIn: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Client.GetAsync(CalendarHost.Calendar)).StatusCode);
    }
}
