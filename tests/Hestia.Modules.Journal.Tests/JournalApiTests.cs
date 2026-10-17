using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.Journal.Application;
using Hestia.Modules.Journal.Contracts;
using Hestia.Modules.Journal.Infrastructure;
using Hestia.Modules.Today.Contracts;
using Hestia.Platform.Events;
using Hestia.Platform.Paging;
using Hestia.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Journal.Tests;

/// <summary>T21: one entry per day, written with PUT, read by day, listed, searched, and deleted.</summary>
public class JournalApiTests
{
    private static readonly DateOnly Today = JournalHost.Today;

    [Fact]
    public async Task AnEntry_IsWrittenOnce_ThenChanged_Read_AndDeleted_WithItsEvents()
    {
        await using var host = JournalHost.Start();

        var created = await host.PutAsync(Today, "  Finished the handouts.\nLong walk by the river.  ");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/journal/entries/2026-10-10", created.Headers.Location?.OriginalString);
        var entry = await created.ReadAsync<JournalEntryDto>();
        Assert.Equal((Today, "Finished the handouts.\nLong walk by the river."), (entry.Day, entry.Text));

        var changed = await host.PutAsync(Today, "Finished the handouts. Then a walk.");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var read = await (await host.Client.GetAsync(JournalHost.EntryUrl(Today))).ReadAsync<JournalEntryDto>();
        Assert.Equal((entry.Id, "Finished the handouts. Then a walk."), (read.Id, read.Text));

        // The same text again changes nothing: no new event.
        Assert.Equal(HttpStatusCode.OK, (await host.PutAsync(Today, "Finished the handouts. Then a walk.")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync(JournalHost.EntryUrl(Today))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(JournalHost.EntryUrl(Today))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync(JournalHost.EntryUrl(Today))).StatusCode);

        var saved = await host.Factory.Events.WaitForAsync<JournalEntrySaved>(e => e.EntryId == entry.Id && !e.Created);
        Assert.Equal(Today, saved.Day);
        await host.Factory.Events.WaitForAsync<JournalEntrySaved>(e => e.EntryId == entry.Id && e.Created);
        await host.Factory.Events.WaitForAsync<JournalEntryDeleted>(e => e.EntryId == entry.Id);
        Assert.Equal(2, (await host.Factory.Events.AfterQuietPeriodAsync<JournalEntrySaved>()).Count(e => e.EntryId == entry.Id));
        // Durable events (T78): the outbox is empty after the delivery.
        Assert.Equal(0, await OutboxCountAsync(host));
    }

    [Theory]
    [InlineData("2026-10-11", "x", "day")]
    [InlineData("2026-10-10", "   ", "text")]
    [InlineData("2026-10-10", null, "text")]
    public async Task AnEntryForTomorrow_OrWithoutText_IsRefused(string day, string? text, string field)
    {
        await using var host = JournalHost.Start();

        var response = await host.PutAsync(DateOnly.Parse(day), text);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AVeryLongText_IsRefused()
    {
        await using var host = JournalHost.Start();

        var response = await host.PutAsync(Today, new string('a', JournalLimits.MaxTextLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await host.PutAsync(Today, new string('a', JournalLimits.MaxTextLength))).StatusCode);
    }

    [Fact]
    public async Task TheSummary_HasTheStreak_TheWeek_TodaysEntry_AndOnThisDay()
    {
        await using var host = JournalHost.Start();
        // Six days in a row up to yesterday, and a gap before them.
        for (var back = 1; back <= 6; back++)
        {
            await host.WriteAsync(Today.AddDays(-back), $"Day {back} ago");
        }
        await host.WriteAsync(Today.AddDays(-9), "Before the gap");
        await host.WriteAsync(new DateOnly(2025, 10, 10), "Moved the desk to the window. Much better light.");
        await host.WriteAsync(new DateOnly(2023, 10, 10), "Three years ago");
        await host.WriteAsync(new DateOnly(2025, 10, 11), "Not this day");

        var before = await host.SummaryAsync();

        // Not written today: the streak up to yesterday still counts.
        Assert.Equal((Today, 6, (JournalEntryDto?)null, 10), (before.Date, before.Streak, before.Today, before.EntryCount));
        Assert.Equal([true, true, true, true, true, true, false], before.Week.Select(d => d.Written));
        Assert.Equal(Today, before.Week[^1].Day);
        Assert.Equal([(1, "Moved the desk to the window. Much better light."), (3, "Three years ago")],
            before.OnThisDay.Select(o => (o.YearsAgo, o.Entry.Excerpt)));

        await host.WriteAsync(Today, "Today");
        var after = await host.SummaryAsync();
        Assert.Equal((7, "Today"), (after.Streak, after.Today?.Text));
        Assert.All(after.Week, day => Assert.True(day.Written));
    }

    [Fact]
    public async Task TheStreak_EndsAfterADayWithoutAnEntry()
    {
        await using var host = JournalHost.Start();
        await host.WriteAsync(Today.AddDays(-2), "Two days ago");

        Assert.Equal(0, (await host.SummaryAsync()).Streak);
    }

    [Fact]
    public void TheExcerpt_DoesNotSplitAnEmoji()
    {
        // The cut falls between the two halves of the emoji: it goes whole.
        var excerpt = JournalService.Excerpt(new string('x', JournalLimits.ExcerptLength - 2) + "🙂 and more");
        Assert.Equal(new string('x', JournalLimits.ExcerptLength - 2) + "…", excerpt);
    }

    [Fact]
    public void Streak_AndSameDays_FollowTheCalendar()
    {
        var days = new HashSet<DateOnly> { new(2026, 3, 1), new(2026, 2, 28), new(2026, 2, 27) };
        Assert.Equal(3, JournalService.Streak(days, new DateOnly(2026, 3, 1)));
        Assert.Equal(3, JournalService.Streak(days, new DateOnly(2026, 3, 2)));
        Assert.Equal(0, JournalService.Streak(days, new DateOnly(2026, 3, 3)));

        // Feb 28 of a common year also shows Feb 29 of the leap years.
        Assert.Equal([new DateOnly(2025, 2, 28), new DateOnly(2024, 2, 28), new DateOnly(2024, 2, 29), new DateOnly(2023, 2, 28)],
            JournalService.SameDays(new DateOnly(2026, 2, 28), 3));
        // Feb 29 shows only the earlier Feb 29s.
        Assert.Equal([new DateOnly(2024, 2, 29), new DateOnly(2020, 2, 29)], JournalService.SameDays(new DateOnly(2028, 2, 29), 8));
    }

    [Fact]
    public async Task TheList_IsNewestFirst_ByPage_BetweenTwoDays_AndSearchesTheText()
    {
        await using var host = JournalHost.Start();
        await host.WriteAsync(Today.AddDays(-3), "Board game night with Marco");
        await host.WriteAsync(Today.AddDays(-2), "Rain all day. Read a book.");
        await host.WriteAsync(Today.AddDays(-1), "Marco came for dinner");
        await host.WriteAsync(Today, $"A very long day. {new string('x', 300)}");

        var all = await ListAsync(host, "");
        Assert.Equal([Today, Today.AddDays(-1), Today.AddDays(-2), Today.AddDays(-3)], all.Items.Select(e => e.Day));
        Assert.EndsWith("…", all.Items[0].Excerpt);
        Assert.Equal(JournalLimits.ExcerptLength, all.Items[0].Excerpt.Length);
        Assert.Equal(317, all.Items[0].Length);

        var page2 = await ListAsync(host, "?page=2&pageSize=3");
        Assert.Equal(4, page2.TotalCount);
        Assert.Equal([Today.AddDays(-3)], page2.Items.Select(e => e.Day));

        var between = await ListAsync(host, $"?from={Today.AddDays(-2):yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}");
        Assert.Equal([Today.AddDays(-1), Today.AddDays(-2)], between.Items.Select(e => e.Day));

        var marco = await ListAsync(host, "?q=marco");
        Assert.Equal([Today.AddDays(-1), Today.AddDays(-3)], marco.Items.Select(e => e.Day).Order().Reverse());

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync($"{JournalHost.Journal}/entries?pageSize=0")).StatusCode);
        // A page so far that the rows before it do not fit an int (it was the first page here, a 500 on PostgreSQL).
        Assert.Equal(HttpStatusCode.BadRequest,
            (await host.Client.GetAsync($"{JournalHost.Journal}/entries?page=2147483647&pageSize=100")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await host.Client.GetAsync($"{JournalHost.Journal}/entries?from=2026-10-10&to=2026-10-01")).StatusCode);
    }

    [Fact]
    public async Task TheSearch_UsesTheFullTextIndex_OnPostgreSql()
    {
        Assert.SkipUnless(TestDatabase.OnPostgres, $"Needs PostgreSQL: set {TestDatabase.Variable}.");
        await using var host = JournalHost.Start();
        await host.WriteAsync(Today.AddDays(-1), "We walked along the rivers for hours");
        await host.WriteAsync(Today, "Nothing about water");

        // "english" stems "walking" and "river" to the words in the text.
        var found = await ListAsync(host, "?q=walking%20river");
        Assert.Equal([Today.AddDays(-1)], found.Items.Select(e => e.Day));
        Assert.Empty((await ListAsync(host, "?q=river%20-walked")).Items);
    }

    [Fact]
    public async Task Today_HasTheJournalTile()
    {
        await using var host = JournalHost.Start();
        await host.WriteAsync(Today.AddDays(-1), "Yesterday");

        var before = Tile(await (await host.Client.GetAsync("/api/today?refresh=true")).ReadAsync<TodayDto>());
        Assert.Equal((1m, "Not written today", TodaySeverity.Neutral, "/journal"), (before.Value!.Number, before.Text, before.Severity, before.Link));

        await host.WriteAsync(Today, "Today");
        var after = Tile(await (await host.Client.GetAsync("/api/today?refresh=true")).ReadAsync<TodayDto>());
        Assert.Equal((2m, null, TodaySeverity.Good), (after.Value!.Number, after.Text, after.Severity));
    }

    [Fact]
    public async Task AReadOnlyUser_ReadsButDoesNotWrite_AndAnonymousIs401()
    {
        await using var host = JournalHost.Start();
        await host.WriteAsync(Today, "Mine");
        host.Client.DefaultRequestHeaders.Add(TestSignIn.ReadOnlyHeader, "yes");

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(JournalHost.EntryUrl(Today))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutAsync(Today, "Changed")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.DeleteAsync(JournalHost.EntryUrl(Today))).StatusCode);

        await using var anonymous = JournalHost.Start(signedIn: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Client.GetAsync($"{JournalHost.Journal}/summary")).StatusCode);
    }

    private static TodayCard Tile(TodayDto today) => Assert.Single(today.Cards, card => card.Id == JournalTodayCards.TileId);

    private static async Task<PaginatedList<JournalEntrySummaryDto>> ListAsync(JournalHost host, string query) =>
        await (await host.Client.GetAsync($"{JournalHost.Journal}/entries{query}")).ReadAsync<PaginatedList<JournalEntrySummaryDto>>();

    private static async Task<int> OutboxCountAsync(JournalHost host)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JournalDbContext>().Set<OutboxMessage>().CountAsync();
    }
}
