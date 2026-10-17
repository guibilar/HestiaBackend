using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.Calendar.Application;
using Hestia.Modules.Calendar.Contracts;
using Hestia.Testing;

namespace Hestia.Modules.Calendar.Tests;

/// <summary>T103: the import of <c>.ics</c> files: repeats, skipped days, time zones, a new import, and cancellations.</summary>
/// <remarks>
/// The tests use Denver, not Edmonton: Alberta keeps UTC-6 from November 2026 (tzdata 2026c), so the result of an
/// Edmonton time would depend on the time zone data of the machine.
/// </remarks>
public class CalendarImportTests
{
    private static string Ics(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//Hestia//EN\r\n" + string.Join("", events) + "END:VCALENDAR\r\n";

    private static string Event(string uid, string body) => $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20261001T000000Z\r\n{body}END:VEVENT\r\n";

    private static readonly string Yoga = Event("yoga@example.com",
        "SUMMARY:Yoga\r\nLOCATION:Studio 2\r\nDTSTART;TZID=America/Denver:20261020T180000\r\nDTEND;TZID=America/Denver:20261020T190000\r\n" +
        "RRULE:FREQ=WEEKLY;COUNT=3\r\nEXDATE;TZID=America/Denver:20261027T180000\r\nDESCRIPTION:Bring a mat\\nand water\r\n");

    private static readonly string Holiday = Event("holiday@example.com",
        "SUMMARY:Long weekend\r\nDTSTART;VALUE=DATE:20261030\r\nDTEND;VALUE=DATE:20261102\r\n");

    // Outlook writes Windows zone names.
    private static readonly string Outlook = Event("outlook@example.com",
        "SUMMARY:Review\r\nDTSTART;TZID=Pacific Standard Time:20261021T090000\r\nDTEND;TZID=Pacific Standard Time:20261021T093000\r\n");

    // No zone: the host's zone (Toronto).
    private static readonly string Floating = Event("floating@example.com", "SUMMARY:Haircut\r\nDTSTART:20261022T150000\r\n");

    [Fact]
    public async Task AFile_IsImported_WithRepeats_SkippedDays_AndTimeZones()
    {
        await using var host = CalendarHost.Start();

        var dryRun = await host.ImportAsync(Ics(Yoga, Holiday, Outlook, Floating), dryRun: true);
        Assert.Equal((true, 4, 0), (dryRun.DryRun, dryRun.Added, dryRun.Updated));
        Assert.DoesNotContain((await host.ViewAsync(CalendarHost.Today, CalendarHost.Today.AddDays(30))).Items, i => i.Module == "Calendar");

        var result = await host.ImportAsync(Ics(Yoga, Holiday, Outlook, Floating));
        Assert.Equal((4, 0, 0, 0, 0), (result.Added, result.Updated, result.Unchanged, result.Deleted, result.Skipped));
        Assert.All(result.Events, e => Assert.NotNull(e.EventId));

        var view = await host.ViewAsync(CalendarHost.Today, CalendarHost.Today.AddDays(30));
        var items = view.Items.Where(i => i.Module == "Calendar").ToList();
        // Yoga: 18:00 in Denver (00:00 UTC the next day); the 27th is skipped, and COUNT=3 ends on November 3rd.
        Assert.Equal(
            [new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 4, 1, 0, 0, TimeSpan.Zero)],
            items.Where(i => i.Title == "Yoga").Select(i => i.StartsAt!.Value));
        var yoga = await (await host.Client.GetAsync($"{CalendarHost.Calendar}/events/{items.First(i => i.Title == "Yoga").EventId}"))
            .ReadAsync<CalendarEventDto>();
        Assert.Equal(("America/Denver", "Studio 2", "Bring a mat\nand water", CalendarEventSource.Import, "yoga@example.com"),
            (yoga.TimeZone, yoga.Location, yoga.Notes, yoga.Source, yoga.Uid));
        Assert.Equal([new DateOnly(2026, 10, 27)], yoga.SkippedDates);
        // An all-day DTEND is the day after the last day.
        var weekend = Assert.Single(items, i => i.Title == "Long weekend");
        Assert.Equal((true, new DateOnly(2026, 10, 30), new DateOnly(2026, 11, 1)), (weekend.AllDay, weekend.StartDate, weekend.EndDate));
        // 09:00 Pacific is 16:00 UTC; the Windows name becomes the IANA zone.
        var review = Assert.Single(items, i => i.Title == "Review");
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 16, 0, 0, TimeSpan.Zero), review.StartsAt);
        var reviewEvent = await (await host.Client.GetAsync($"{CalendarHost.Calendar}/events/{review.EventId}")).ReadAsync<CalendarEventDto>();
        Assert.Equal("America/Los_Angeles", reviewEvent.TimeZone);
        // 15:00 floating is 15:00 in Toronto (19:00 UTC), and it has no length.
        var haircut = Assert.Single(items, i => i.Title == "Haircut");
        Assert.Equal((new DateTimeOffset(2026, 10, 22, 19, 0, 0, TimeSpan.Zero), haircut.StartsAt), (haircut.EndsAt!.Value, haircut.StartsAt));
    }

    [Fact]
    public async Task TheSameFileAgain_ChangesNothing_AChangedEventUpdates_AndACancelledOneIsDeleted()
    {
        await using var host = CalendarHost.Start();
        await host.ImportAsync(Ics(Yoga, Holiday));

        var again = await host.ImportAsync(Ics(Yoga, Holiday));
        Assert.Equal((0, 0, 2), (again.Added, again.Updated, again.Unchanged));

        var moved = await host.ImportAsync(Ics(Holiday.Replace("SUMMARY:Long weekend", "SUMMARY:Long weekend away")));
        Assert.Equal(CalendarImportAction.Update, Assert.Single(moved.Events).Action);

        var cancel = await host.ImportAsync(Ics(Holiday.Replace("SUMMARY:", "STATUS:CANCELLED\r\nSUMMARY:")));
        Assert.Equal((1, CalendarImportAction.Delete), (cancel.Deleted, cancel.Events[0].Action));
        var view = await host.ViewAsync(CalendarHost.Today, CalendarHost.Today.AddDays(30));
        Assert.DoesNotContain(view.Items, i => i.Title.StartsWith("Long weekend", StringComparison.Ordinal));

        // METHOD:CANCEL cancels every event of the file; an event that Hestia does not have is skipped.
        var cancelAll = await host.ImportAsync(Ics(Yoga, Outlook).Replace("VERSION:2.0", "VERSION:2.0\r\nMETHOD:CANCEL"));
        Assert.Equal([CalendarImportAction.Delete, CalendarImportAction.Skip], cancelAll.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task AChangedOccurrence_IsItsOwnEvent_AndTheRepeatSkipsItsDay()
    {
        await using var host = CalendarHost.Start();
        var moved = Event("yoga@example.com",
            "RECURRENCE-ID;TZID=America/Denver:20261103T180000\r\nSUMMARY:Yoga (outdoor)\r\n" +
            "DTSTART;TZID=America/Denver:20261103T170000\r\nDTEND;TZID=America/Denver:20261103T180000\r\n");

        // The changed occurrence comes first in the file; the import handles the repeated event first.
        var result = await host.ImportAsync(Ics(moved, Yoga));
        Assert.Equal(2, result.Added);

        var items = (await host.ViewAsync(CalendarHost.Today, CalendarHost.Today.AddDays(30))).Items
            .Where(i => i.Title.StartsWith("Yoga", StringComparison.Ordinal)).ToList();
        Assert.Equal(["Yoga", "Yoga (outdoor)"], items.Select(i => i.Title));
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero), items[1].StartsAt);
    }

    [Fact]
    public async Task ACancelledRepeatedEvent_IsDeleted_WithItsChangedOccurrences()
    {
        await using var host = CalendarHost.Start();
        var moved = Event("yoga@example.com",
            "RECURRENCE-ID;TZID=America/Denver:20261103T180000\r\nSUMMARY:Yoga (outdoor)\r\n" +
            "DTSTART;TZID=America/Denver:20261103T170000\r\nDTEND;TZID=America/Denver:20261103T180000\r\n");
        await host.ImportAsync(Ics(Yoga, moved));

        var cancel = await host.ImportAsync(Ics(Yoga.Replace("SUMMARY:", "STATUS:CANCELLED\r\nSUMMARY:")));

        Assert.Equal(CalendarImportAction.Delete, Assert.Single(cancel.Events).Action);
        var items = (await host.ViewAsync(CalendarHost.Today, CalendarHost.Today.AddDays(30))).Items;
        Assert.DoesNotContain(items, i => i.Title.StartsWith("Yoga", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACancelledOccurrence_IsSkippedByTheRepeat()
    {
        await using var host = CalendarHost.Start();
        await host.ImportAsync(Ics(Yoga));
        // Outlook and Google send this when the organizer deletes one occurrence: Hestia has no copy of it.
        var cancelled = Event("yoga@example.com",
            "RECURRENCE-ID;TZID=America/Denver:20261103T180000\r\nSTATUS:CANCELLED\r\nSUMMARY:Yoga\r\n" +
            "DTSTART;TZID=America/Denver:20261103T180000\r\nDTEND;TZID=America/Denver:20261103T190000\r\n");

        var result = await host.ImportAsync(Ics(cancelled));
        Assert.Equal(CalendarImportAction.Update, Assert.Single(result.Events).Action);

        var items = (await host.ViewAsync(CalendarHost.Today, CalendarHost.Today.AddDays(30))).Items.Where(i => i.Title == "Yoga");
        Assert.Equal([new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero)], items.Select(i => i.StartsAt!.Value));
        Assert.Equal(CalendarImportAction.Same, Assert.Single((await host.ImportAsync(Ics(cancelled))).Events).Action);
    }

    [Fact]
    public async Task ARepeatFasterThanDaily_IsSkipped_WithAReason_AndABadFileIsRefused()
    {
        await using var host = CalendarHost.Start();
        var hourly = Event("hourly@example.com", "SUMMARY:Water\r\nDTSTART:20261020T090000Z\r\nRRULE:FREQ=HOURLY\r\n");

        var result = await host.ImportAsync(Ics(hourly, Holiday));
        Assert.Equal((1, 1), (result.Added, result.Skipped));
        Assert.Contains("daily", result.Events[0].Reason);

        var bad = await host.Client.PostAsJsonAsync($"{CalendarHost.Calendar}/import", new CalendarImportRequest("hello"), HestiaApiFactory.Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("ics", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public void ALongTitle_IsNotCutInsideAnEmoji()
    {
        // Half of an emoji is not valid text: PostgreSQL refuses it.
        var title = new string('x', CalendarLimits.MaxTitleLength - 1) + "😀";
        var ics = Ics(Event("emoji@example.com", $"SUMMARY:{title}\r\nDTSTART:20261020T090000Z\r\n"));

        var e = Assert.Single(IcsReader.Read(ics, TimeZoneInfo.Utc));

        Assert.Equal(new string('x', CalendarLimits.MaxTitleLength - 1), e.Title);
    }
}
