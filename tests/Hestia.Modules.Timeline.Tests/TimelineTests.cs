using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Hestia.Modules.Timeline.Contracts;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hestia.Modules.Timeline.Tests;

/// <summary>A clock that a test sets and moves.</summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// T16: the Timeline merges the entries of every module, day by day, newest first, from their own data. Money's come
/// from finance (transactions, and the month review of T82). The host's clock is noon in Toronto on Saturday,
/// October 10th, 2026.
/// </summary>
public class TimelineTests
{
    private const string TimeZone = "America/Toronto";
    private static readonly DateTimeOffset Noon = new(2026, 10, 10, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 10);

    private sealed class Host : IAsyncDisposable
    {
        public required HestiaApiFactory Factory { get; init; }
        public required TestClock Clock { get; init; }
        public required FakeFinance Finance { get; init; }
        public required FinanceData Data { get; init; }
        public HttpClient Client => _client ??= Factory.CreateClient();
        private HttpClient? _client;

        public async Task<TimelineDto> TimelineAsync(string query = "")
        {
            var response = await Client.GetAsync($"/api/timeline{query}");
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return await response.ReadAsync<TimelineDto>();
        }

        public async Task<T> PostAsync<T>(string url, object body)
        {
            var response = await Client.PostAsJsonAsync(url, body, HestiaApiFactory.Json);
            Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync()}");
            return await response.ReadAsync<T>();
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            await Factory.DisposeAsync();
            await Finance.DisposeAsync();
        }
    }

    private sealed record Created(Guid Id);

    private sealed record CreatedPerson(Created Person);

    private static async Task<Host> StartAsync(Action<FinanceData>? finance = null)
    {
        var data = new FinanceData();
        data.Answers["POST /api/Transaction/Filter"] = new
        {
            items = new object[]
            {
                Transaction("0199b0b0-0000-7000-8000-000000000001", "2026-10-10T00:00:00Z", "COSTCO", -142.18m, "Groceries"),
                Transaction("0199b0b0-0000-7000-8000-000000000002", "2026-10-02T00:00:00Z", "PAYROLL", 2400m, "Salary"),
            },
            page = 1, pageSize = 1000, totalCount = 2
        };
        data.Answers["GET /api/Reports/review"] = new { spending = 1234.5m, purchases = 41, noSpendDays = 6 };
        finance?.Invoke(data);
        var fake = await FakeFinance.StartAsync(data.RespondAsync);
        var clock = new TestClock(Noon);
        var settings = fake.HostSettings(new Dictionary<string, string?>
        {
            ["Hestia:TimeZone"] = TimeZone,
            ["Documents:Ocr:Enabled"] = "false"
        });
        var factory = new HestiaApiFactory(settings, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        return new Host { Factory = factory, Clock = clock, Finance = fake, Data = data };
    }

    private static object Transaction(string id, string date, string description, decimal amount, string category) => new
    {
        id, date, description, amount, category = new { id = Guid.NewGuid(), name = category }, accountName = "CIBC Visa",
        accountId = Guid.NewGuid(), hidden = false, tags = Array.Empty<object>()
    };

    private static async Task SeedAsync(Host host)
    {
        // Yesterday evening in Toronto: a person, a contact, a document, an item, and a task done.
        host.Clock.Now = new DateTimeOffset(2026, 10, 10, 2, 30, 0, TimeSpan.Zero); // Oct 9, 22:30 in Toronto
        var person = await host.PostAsync<CreatedPerson>("/api/people", new { name = "Marco Silva", relationship = "Friend" });
        await host.PostAsync<Created>($"/api/people/{person.Person.Id}/interactions", new { kind = "Coffee", occurredOn = "2026-10-08", note = "New job" });
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Car insurance policy"), "title");
        form.Add(new StringContent("Insurance"), "kind");
        var file = new ByteArrayContent("%PDF-1.4\n%%EOF\n"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "files", "policy.pdf");
        var upload = await host.Client.PostAsync("/api/documents", form);
        Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync());
        var room = await host.PostAsync<Created>("/api/home/rooms", new { name = "Kitchen" });
        await host.PostAsync<Created>("/api/home/items", new { name = "Dishwasher", roomId = room.Id });
        var task = await host.PostAsync<Created>("/api/tasks", new { title = "Change the furnace filter" });
        await host.PostAsync<Created>($"/api/tasks/{task.Id}/done", new { });

        // Today at noon: the journal entry.
        host.Clock.Now = Noon;
        var journal = await host.Client.PutAsJsonAsync("/api/journal/entries/2026-10-10", new { text = "Finished the handouts.\nLong walk." });
        Assert.True(journal.IsSuccessStatusCode, await journal.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheTimeline_MergesEveryModule_DayByDay_NewestFirst()
    {
        await using var host = await StartAsync();
        await SeedAsync(host);

        var timeline = await host.TimelineAsync();

        Assert.Equal((Today, new DateOnly(2026, 10, 4), Today, new DateOnly(2026, 10, 4)),
            (timeline.Today, timeline.From, timeline.To, timeline.Earlier));
        Assert.All(timeline.Modules, module => Assert.Equal(TimelineModuleStatus.Complete, module.Status));
        Assert.Equal(["Documents", "Home", "Journal", "Money", "People", "Tasks"], timeline.Modules.Select(m => m.Module));
        Assert.Equal([Today, new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 8)], timeline.Days.Select(d => d.Day));

        // Today: the transaction and the journal entry (both for the whole day), by module.
        Assert.Equal(
            [("money.transaction.0199b0b0-0000-7000-8000-000000000001", "COSTCO", "Groceries · CIBC Visa", (decimal?)-142.18m),
             ("journal", "Journal", "Finished the handouts. Long walk.", null)],
            timeline.Days[0].Entries.Select(e => (e.Module == "Journal" ? "journal" : e.Id, e.Title, e.Text, e.Amount))
                .OrderBy(e => e.Item1 == "journal" ? 1 : 0));
        Assert.Contains(timeline.Days[0].Entries, e => e.Link == "/money/transactions?id=0199b0b0-0000-7000-8000-000000000001");

        // Yesterday evening in Toronto (after midnight UTC): the things added, the task done.
        var yesterday = timeline.Days[1].Entries;
        Assert.Equal(
            ["Added Marco Silva", "Car insurance policy", "Change the furnace filter", "Dishwasher"],
            yesterday.Select(e => e.Title).Order());
        Assert.All(yesterday, e => Assert.NotNull(e.At));
        Assert.Contains(yesterday, e => e is { Kind: "document-added", Text: "Insurance" } && e.Link!.StartsWith("/documents/"));
        Assert.Contains(yesterday, e => e is { Kind: "item-added", Text: "Added to Kitchen" });
        Assert.Contains(yesterday, e => e is { Kind: "task-done", Text: "Done" } && e.Link!.StartsWith("/tasks/"));

        var contact = Assert.Single(timeline.Days[2].Entries);
        Assert.Equal(("contact", "Coffee with Marco Silva", "New job", (DateTimeOffset?)null), (contact.Kind, contact.Title, contact.Text, contact.At));

        // Finance got the days of the page, as the signed-in user.
        var request = host.Finance.Requests.Single(r => r.Target == "/api/Transaction/Filter");
        Assert.Contains("\"startDate\":\"2026-10-04T00:00:00Z\"", request.Body);
        Assert.Contains("\"endDate\":\"2026-10-10T00:00:00Z\"", request.Body);
        Assert.Equal(TestSignIn.UserId, GatewayToken.SubjectOf(request));
    }

    [Fact]
    public async Task ThePreviousPage_EndsTheDayBeforeEarlier_AndHasTheMonthReview()
    {
        await using var host = await StartAsync();

        var page = await host.TimelineAsync("?before=2026-10-04&days=7");

        Assert.Equal((new DateOnly(2026, 9, 27), new DateOnly(2026, 10, 3)), (page.From, page.To));
        var october1 = Assert.Single(page.Days, d => d.Day == new DateOnly(2026, 10, 1));
        var review = Assert.Single(october1.Entries);
        Assert.Equal(("money.month-review.2026-09", "September in review", "41 purchases, 6 days without spending", (decimal?)-1234.5m,
            "/money/review?year=2026&month=9"), (review.Id, review.Title, review.Text, review.Amount, review.Link));
        Assert.Contains(host.Finance.Requests, r => r.Target == "/api/Reports/review?year=2026&month=9");
        // The transaction of Oct 2 is on this page; the one of today is not (finance gave it, but it is outside the days).
        Assert.Contains(page.Days.SelectMany(d => d.Entries), e => e.Title == "PAYROLL");
        Assert.DoesNotContain(page.Days.SelectMany(d => d.Entries), e => e.Title == "COSTCO");
    }

    [Fact]
    public async Task OneModule_AndAPageAfterToday_EndsToday()
    {
        await using var host = await StartAsync();
        await SeedAsync(host);

        var money = await host.TimelineAsync("?module=money&before=2026-12-01&days=3");

        Assert.Equal((new DateOnly(2026, 10, 8), Today), (money.From, money.To));
        Assert.Equal(["Money"], money.Modules.Select(m => m.Module));
        Assert.All(money.Days.SelectMany(d => d.Entries), e => Assert.Equal("Money", e.Module));
    }

    [Fact]
    public async Task FinanceDown_FailsMoneyOnly()
    {
        await using var host = await StartAsync(data => data.Failures["POST /api/Transaction/Filter"] = 502);
        await SeedAsync(host);

        var timeline = await host.TimelineAsync();

        Assert.Equal(TimelineModuleStatus.Failed, timeline.Modules.Single(m => m.Module == "Money").Status);
        Assert.All(timeline.Modules.Where(m => m.Module != "Money"), m => Assert.Equal(TimelineModuleStatus.Complete, m.Status));
        Assert.DoesNotContain(timeline.Days.SelectMany(d => d.Entries), e => e.Module == "Money");
        Assert.Contains(timeline.Days.SelectMany(d => d.Entries), e => e.Module == "Journal");
    }

    [Fact]
    public async Task AReviewThatDoesNotLoad_MakesMoneyPartial()
    {
        await using var host = await StartAsync(data => data.Failures["GET /api/Reports/review"] = 502);

        var page = await host.TimelineAsync("?before=2026-10-04");

        Assert.Equal(TimelineModuleStatus.Partial, page.Modules.Single(m => m.Module == "Money").Status);
        Assert.Contains(page.Days.SelectMany(d => d.Entries), e => e.Title == "PAYROLL");
    }

    [Theory]
    [InlineData("?days=0")]
    [InlineData("?days=32")]
    [InlineData("?module=Recipes")]
    // Year 1: the page would start before 0001-01-01 (it was a 500).
    [InlineData("?before=0001-01-01")]
    [InlineData("?before=0001-01-05&days=7")]
    public async Task BadParameters_Are400(string query)
    {
        await using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync($"/api/timeline{query}")).StatusCode);
    }

    [Fact]
    public async Task AReadOnlyUser_ReadsTheTimeline()
    {
        await using var host = await StartAsync();
        host.Client.DefaultRequestHeaders.Add(TestSignIn.ReadOnlyHeader, "yes");

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/timeline")).StatusCode);
    }

    [Fact]
    public void TheRange_GivesUtcMoments_AndLocalDays()
    {
        var range = new TimelineRange(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), TimeZoneInfo.FindSystemTimeZoneById(TimeZone));

        // Nov 1, 2026 is the end of daylight time in Toronto: the day has 25 hours.
        Assert.Equal((new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero)),
            (range.Start, range.End));
        Assert.Equal(TimeSpan.Zero, range.Start.Offset);
        Assert.Equal(new DateOnly(2026, 10, 31), range.DayOf(new DateTimeOffset(2026, 11, 1, 3, 59, 0, TimeSpan.Zero)));
    }
}
