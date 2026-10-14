using System.Net;
using System.Text.Json;
using Hestia.Modules.Today.Contracts;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hestia.Modules.Today.Tests;

/// <summary>GET /api/today through the real host, with the finance reports from <see cref="FakeFinance"/>.</summary>
public sealed class TodayEndpointTests
{
    private const string Today = "/api/today";

    /// <summary>A clock that stops at one moment.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A Today provider of another module, for the tests of order and failures.</summary>
    private sealed class OtherProvider(string module, int order, Func<CancellationToken, Task<TodayModuleCards>> cards) : ITodayCardProvider
    {
        public string Module => module;
        public int Order => order;
        public Task<TodayModuleCards> GetCardsAsync(TodayContext context, CancellationToken cancellationToken) => cards(cancellationToken);
    }

    private static TodayCard Card(string module, TodayCardType type, string kind) =>
        new($"{module.ToLowerInvariant()}.{kind}", module, type, kind, kind, null, null,
            new Dictionary<string, TodayValue>(), TodaySeverity.Neutral, null, null);

    private static HestiaApiFactory Host(FakeFinance finance, DateTimeOffset? now = null,
        Dictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    {
        var all = new Dictionary<string, string?> { ["Today:TimeZone"] = FinanceData.TimeZone };
        foreach (var (key, value) in settings ?? [])
        {
            all[key] = value;
        }
        return new HestiaApiFactory(finance.HostSettings(all), configure =>
        {
            // These tests look at the Money cards and at the providers that a test adds. The providers of other modules
            // (Documents) have their own tests.
            foreach (var provider in configure.Where(service => service.ServiceType == typeof(ITodayCardProvider) &&
                         service.ImplementationType?.Namespace?.StartsWith("Hestia.Modules.Today.", StringComparison.Ordinal) == false).ToList())
            {
                configure.Remove(provider);
            }
            if (now is { } moment)
            {
                configure.RemoveAll<TimeProvider>();
                configure.AddSingleton<TimeProvider>(new FixedTime(moment));
            }
            services?.Invoke(configure);
        });
    }

    private static async Task<TodayDto> GetTodayAsync(HttpClient client, string path = Today)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<TodayDto>();
    }

    private static TodayCard Single(TodayDto today, string id) => Assert.Single(today.Cards, card => card.Id == id);

    [Fact]
    public async Task WithoutSignIn_Is401()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = new HestiaApiFactory(finance.HostSettings(), signedIn: false);

        var response = await factory.CreateClient().GetAsync(Today);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(finance.Requests);
    }

    [Fact]
    public async Task FinanceCards_MatchTheWebDashboard_ForTheSameData()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = Host(finance, FinanceData.Now);

        var today = await GetTodayAsync(factory.CreateClient());

        Assert.Equal(FinanceData.Today, today.Date);
        Assert.Equal(new TodayModuleDto("Money", TodayModuleStatus.Complete), Assert.Single(today.Modules));
        Assert.Equal(
            new[]
            {
                "money.spent-this-month", "money.left-over-this-month", "money.net-worth", "money.budgets",
                $"money.budget-over.{FinanceData.GroceriesId}", "money.statements-not-imported",
                $"money.statements-behind.{FinanceData.TfsaId}", "money.price-changed.NETFLIX", "money.unsorted"
            },
            today.Cards.Select(card => card.Id));

        // Spent this month: 400 against a usual month by now (3000 × 5 / 30 = 500).
        var spent = Single(today, "money.spent-this-month");
        Assert.Equal((TodayCardType.Tile, "Spent this month", (decimal?)400m), (spent.Type, spent.Title, spent.Value!.Number));
        Assert.Equal("{difference} under a usual month by now", spent.Text);
        Assert.Equal(TodayValue.Money(100m), spent.Values["difference"]);
        Assert.Equal((TodaySeverity.Good, TodayTrend.Down), (spent.Severity, spent.Trend));

        var leftOver = Single(today, "money.left-over-this-month");
        Assert.Equal(TodayValue.Money(1600m), leftOver.Value);
        Assert.Equal(("Income less spending so far", TodaySeverity.Good), (leftOver.Text, leftOver.Severity));

        var netWorth = Single(today, "money.net-worth");
        Assert.Equal(TodayValue.Money(10500m), netWorth.Value);
        Assert.Equal("+{change} since last month", netWorth.Text);
        Assert.Equal(TodayValue.Money(500m), netWorth.Values["change"]);
        Assert.Equal((TodaySeverity.Good, TodayTrend.Up, "/money/net-worth"), (netWorth.Severity, netWorth.Trend, netWorth.Link));

        var budgets = Single(today, "money.budgets");
        Assert.Equal(TodayValue.Plain("1 of 2 over"), budgets.Value);
        Assert.Equal(("Review your budgets", TodaySeverity.Bad, "/money/budgets"), (budgets.Text, budgets.Severity, budgets.Link));

        // Needs attention: the same items, in the same order, as attention.spec.ts (plus the import reminder).
        var budget = Single(today, $"money.budget-over.{FinanceData.GroceriesId}");
        Assert.Equal((TodayCardType.Attention, "{budget} is over its limit ({used} used)", TodaySeverity.Bad),
            (budget.Type, budget.Text, budget.Severity));
        Assert.Equal(TodayValue.Plain("Groceries"), budget.Values["budget"]);
        Assert.Equal(TodayValue.Percent(120m), budget.Values["used"]);

        var notImported = Single(today, "money.statements-not-imported");
        Assert.Equal(TodayValue.Count(9), notImported.Values["days"]);
        Assert.Equal("/money/transactions#import", notImported.Link);

        var behind = Single(today, $"money.statements-behind.{FinanceData.TfsaId}");
        Assert.Equal("{account}: statements reach {reach}. Import last month's", behind.Text);
        Assert.Equal(TodayValue.Day(new DateOnly(2026, 6, 30)), behind.Values["reach"]);
        Assert.Equal("/money/transactions#import", behind.Link);

        var price = Single(today, "money.price-changed.NETFLIX");
        Assert.Equal("{payment} went up from {from} to {to}", price.Text);
        Assert.Equal((TodayValue.Money(16.49m), TodayValue.Money(18.99m)), (price.Values["from"], price.Values["to"]));
        Assert.Equal("/money/subscriptions", price.Link);

        var unsorted = Single(today, "money.unsorted");
        Assert.Equal("{count} transaction is in Other because no rule names its merchant", unsorted.Text);
        Assert.Equal("/money/rules#unsorted", unsorted.Link);
    }

    [Fact]
    public async Task FinanceRequests_UseTheSignedInUser_AndTheBudgetQueryAsksForAllActiveBudgets()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        // The real clock: finance checks the lifetime of the token.
        await using var factory = Host(finance);

        await GetTodayAsync(factory.CreateClient());

        Assert.Equal(FinanceData.Paths.Order(), finance.Requests.Select(r => $"{r.Method} {r.Target}").Order());
        Assert.All(finance.Requests, request =>
            Assert.Equal((TestSignIn.UserId, "full"), (GatewayToken.Read(request).Subject, GatewayToken.Read(request).Scope)));
        var filter = JsonDocument.Parse(Assert.Single(finance.Requests, r => r.Method == "POST").Body).RootElement;
        Assert.Equal(("Active", 1, 1000),
            (filter.GetProperty("status").GetString(), filter.GetProperty("page").GetInt32(), filter.GetProperty("pageSize").GetInt32()));
    }

    [Fact]
    public async Task AReadOnlyUser_CanCallIt_AndFinanceGetsTheReadOnlyScope()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = Host(finance);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestSignIn.ReadOnlyHeader, "yes");

        var today = await GetTodayAsync(client);

        Assert.Equal(TodayModuleStatus.Complete, Assert.Single(today.Modules).Status);
        Assert.NotEmpty(finance.Requests);
        Assert.All(finance.Requests, request => Assert.Equal("read-only", GatewayToken.Read(request).Scope));
    }

    [Fact]
    public async Task OneReportFails_ItsTileHasNoValue_AndTheOtherCardsStay()
    {
        var data = new FinanceData();
        data.Failures["GET /api/Reports/net-worth"] = 500;
        data.Failures["GET /api/Reports/recurring"] = 503;
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = Host(finance, FinanceData.Now);

        var today = await GetTodayAsync(factory.CreateClient());

        Assert.Equal(TodayModuleStatus.Partial, Assert.Single(today.Modules).Status);
        var netWorth = Single(today, "money.net-worth");
        Assert.Null(netWorth.Value);
        Assert.Equal("Could not load this", netWorth.Text);
        Assert.Equal(TodayValue.Money(400m), Single(today, "money.spent-this-month").Value);
        // The price change came from the recurring report: no item. The others stay.
        Assert.DoesNotContain(today.Cards, card => card.Kind == "price-changed");
        Assert.Contains(today.Cards, card => card.Kind == "unsorted");
    }

    [Fact]
    public async Task FinanceDown_TheMoneyModuleFails_AndTheAnswerIsStill200()
    {
        var data = new FinanceData();
        foreach (var path in FinanceData.Paths)
        {
            data.Failures[path] = 502;
        }
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = Host(finance, FinanceData.Now);

        var today = await GetTodayAsync(factory.CreateClient());

        Assert.Equal(new TodayModuleDto("Money", TodayModuleStatus.Failed), Assert.Single(today.Modules));
        Assert.Empty(today.Cards);
    }

    [Fact]
    public async Task AProviderThatFails_OrDoesNotAnswer_GivesNoCards_AndTheOthersStay()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = Host(finance, FinanceData.Now,
            new() { ["Today:ProviderTimeout"] = "00:00:02" },
            services =>
            {
                services.AddScoped<ITodayCardProvider>(_ => new OtherProvider("Broken", 100,
                    _ => throw new InvalidOperationException("Broken on purpose.")));
                services.AddScoped<ITodayCardProvider>(_ => new OtherProvider("Slow", 200, async cancellationToken =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                    return new TodayModuleCards([]);
                }));
            });

        var today = await GetTodayAsync(factory.CreateClient());

        Assert.Equal(
            new[] { ("Money", TodayModuleStatus.Complete), ("Broken", TodayModuleStatus.Failed), ("Slow", TodayModuleStatus.Failed) },
            today.Modules.Select(m => (m.Module, m.Status)));
        Assert.All(today.Cards, card => Assert.Equal("Money", card.Module));
        Assert.Equal(9, today.Cards.Count);
    }

    [Fact]
    public async Task Cards_AreInModuleOrder_TilesFirst_FinanceFirst()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = Host(finance, FinanceData.Now, services: services =>
            services.AddScoped<ITodayCardProvider>(_ => new OtherProvider("Documents", 100, _ => Task.FromResult(
                new TodayModuleCards(
                [
                    Card("Documents", TodayCardType.Attention, "expiring"),
                    Card("Documents", TodayCardType.Tile, "count"),
                    // Another module's card: left out.
                    Card("Money", TodayCardType.Tile, "fake")
                ])))));

        var today = await GetTodayAsync(factory.CreateClient());

        Assert.Equal(new[] { "Money", "Documents" }, today.Modules.Select(m => m.Module));
        Assert.Equal(TodayModuleStatus.Complete, today.Modules[1].Status);
        var order = today.Cards.Select(card => $"{card.Module}:{card.Type}").Distinct().ToList();
        Assert.Equal(new[] { "Money:Tile", "Documents:Tile", "Money:Attention", "Documents:Attention" }, order);
        Assert.DoesNotContain(today.Cards, card => card.Id == "money.fake");
    }

    [Fact]
    public async Task TheAnswer_IsKeptForEachUser_AndRefreshMakesANewOne()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = Host(finance, FinanceData.Now);
        var client = factory.CreateClient();
        var readOnly = factory.CreateClient();
        readOnly.DefaultRequestHeaders.Add(TestSignIn.ReadOnlyHeader, "yes");
        var perAnswer = FinanceData.Paths.Length;

        var first = await GetTodayAsync(client);
        var second = await GetTodayAsync(client);
        Assert.Equal(perAnswer, finance.Requests.Count);
        Assert.Equal(first.GeneratedAt, second.GeneratedAt);

        // The read-only scope has its own answer.
        await GetTodayAsync(readOnly);
        Assert.Equal(2 * perAnswer, finance.Requests.Count);

        await GetTodayAsync(client, $"{Today}?refresh=true");
        Assert.Equal(3 * perAnswer, finance.Requests.Count);
    }

    [Fact]
    public async Task APartialAnswer_IsNotKept()
    {
        var data = new FinanceData();
        data.Failures["GET /api/Accounts"] = 500;
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = Host(finance, FinanceData.Now);
        var client = factory.CreateClient();

        await GetTodayAsync(client);
        data.Failures.Clear();
        var second = await GetTodayAsync(client);

        Assert.Equal(TodayModuleStatus.Complete, Assert.Single(second.Modules).Status);
        Assert.Equal(2 * FinanceData.Paths.Length, finance.Requests.Count);
    }

    [Fact]
    public async Task TheDay_IsTheDayInTheTimeZoneOfTheHost()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        // 03:00 UTC on October 1st is still September 30th in Toronto.
        await using var factory = Host(finance, new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.Zero));

        var today = await GetTodayAsync(factory.CreateClient());

        Assert.Equal(new DateOnly(2026, 9, 30), today.Date);
    }

    [Fact]
    public async Task AnUnknownTimeZone_StopsTheHost()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = Host(finance, settings: new() { ["Today:TimeZone"] = "Mars/Olympus" });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Today:TimeZone", error.ToString());
    }

    [Fact]
    public async Task TheOpenApiDocument_DescribesTheTodayEndpoint()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = new HestiaApiFactory(finance.HostSettings());

        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var get = document.RootElement.GetProperty("paths").GetProperty("/api/today").GetProperty("get");
        Assert.Equal("GetToday", get.GetProperty("operationId").GetString());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("TodayDto", out _));
        Assert.True(schemas.TryGetProperty("TodayCard", out _));
        // The finance paths are not module endpoints.
        Assert.False(document.RootElement.GetProperty("paths").TryGetProperty("/api/Accounts", out _));
    }
}
