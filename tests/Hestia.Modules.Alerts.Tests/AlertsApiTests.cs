using System.Net;
using System.Text.Json;
using Hestia.Contracts.Finance;
using Hestia.Modules.Alerts.Application.Finance;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Alerts.Domain;
using Hestia.Modules.Alerts.Infrastructure;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Hestia.Modules.Alerts.Tests.AlertsHost;

namespace Hestia.Modules.Alerts.Tests;

/// <summary>
/// The Alerts module through the real host (T15): GET /api/alerts, the finance rules on the reports of
/// <see cref="FakeFinance"/>, the store (raise, update, resolve), the finance events, and the timer.
/// </summary>
public sealed class AlertsApiTests
{
    private static readonly string[] MoneyKeys =
    [
        $"money.budget-over.{FinanceData.GroceriesId}", "money.statements-not-imported",
        $"money.statements-behind.{FinanceData.TfsaId}", "money.price-changed.NETFLIX", "money.unsorted"
    ];

    [Fact]
    public async Task WithoutSignIn_Is401()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = new HestiaApiFactory(finance.HostSettings(), signedIn: false);

        var response = await factory.CreateClient().GetAsync(AlertsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(finance.Requests);
    }

    [Fact]
    public async Task TheFinanceAlerts_AreTheTodayItems_MostSevereFirst()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = Start(finance, new TestClock(FinanceData.Now));

        // The first request after the start waits for the first check.
        var list = await GetAlertsAsync(factory.CreateClient());

        Assert.Equal(new AlertModuleDto("Money", 0, AlertModuleStatus.Complete), Assert.Single(list.Modules));
        Assert.Equal(FinanceData.Now, list.CheckedAt);
        // The budget is Bad, the others Warning; the same time, so the order of the rules.
        Assert.Equal(MoneyKeys, list.Alerts.Select(alert => alert.Id));
        Assert.All(list.Alerts, alert => Assert.Equal(("Money", "Attention", FinanceData.Now), (alert.Module, alert.Type, alert.RaisedAt)));

        var budget = list.Alerts[0];
        Assert.Equal(("budget-over", "Budget over its limit", "{budget} is over its limit ({used} used)", AlertSeverity.Bad, "/money/budgets"),
            (budget.Kind, budget.Title, budget.Text, budget.Severity, budget.Link));
        Assert.Equal(AlertValue.Plain("Groceries"), budget.Values["budget"]);
        Assert.Equal(AlertValue.Percent(120m), budget.Values["used"]);
        Assert.Equal(AlertValue.Count(9), list.Alerts[1].Values["days"]);
        Assert.Equal(AlertValue.Day(new DateOnly(2026, 6, 30)), list.Alerts[2].Values["reach"]);
        Assert.Equal((AlertValue.Money(16.49m), AlertValue.Money(18.99m)), (list.Alerts[3].Values["from"], list.Alerts[3].Values["to"]));

        // The check read the finance reports as the system identity, read-only.
        var system = FinanceData.SystemRequests(finance);
        Assert.Equal(FinanceData.AlertPaths.Order(), system.Select(r => $"{r.Method} {r.Target}").Order());
        Assert.Equal(system.Count, finance.Requests.Count);
    }

    [Fact]
    public async Task AReadOnlyUser_CanCallIt()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = Start(finance);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestSignIn.ReadOnlyHeader, "yes");

        var list = await GetAlertsAsync(client);

        Assert.Equal(MoneyKeys.Order(), list.Alerts.Select(alert => alert.Id).Order());
        // The real clock: finance checks the token. The checks run as the system identity, read-only.
        Assert.All(finance.Requests, request =>
            Assert.Equal(("hestia-system", "system:Alerts.FinanceAlertSource", "read-only"),
                (GatewayToken.Read(request).Subject, GatewayToken.Read(request).Name, GatewayToken.Read(request).Scope)));
    }

    [Fact]
    public async Task AConditionThatEnds_IsResolved_AndWhenItComesBack_ItIsANewAlert()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        var clock = new TestClock(FinanceData.Now);
        await using var factory = Start(finance, clock);
        var client = factory.CreateClient();
        var budgetKey = $"money.budget-over.{FinanceData.GroceriesId}";
        var first = Assert.Single((await GetAlertsAsync(client)).Alerts, alert => alert.Id == budgetKey);

        // The budget is under its limit now.
        SetBudgets(data, FinanceData.BudgetJson(FinanceData.GroceriesId, "Groceries", 150m, 135m, 90m));
        clock.Now = FinanceData.Now.AddMinutes(10);
        var list = await GetAlertsAsync(client, refresh: true);

        Assert.DoesNotContain(list.Alerts, alert => alert.Id == budgetKey);
        Assert.Equal(MoneyKeys.Skip(1), list.Alerts.Select(alert => alert.Id));
        var resolved = Assert.Single(await RowsAsync(factory), row => row.Key == budgetKey);
        Assert.Equal((first.AlertId, (DateTimeOffset?)clock.Now), (resolved.Id, resolved.ResolvedAt));

        // Over again: a new alert, with a new id and a new start.
        SetBudgets(data, FinanceData.BudgetJson(FinanceData.GroceriesId, "Groceries", 150m, 180m, 120m));
        clock.Now = FinanceData.Now.AddMinutes(20);
        var again = Assert.Single((await GetAlertsAsync(client, refresh: true)).Alerts, alert => alert.Id == budgetKey);

        Assert.NotEqual(first.AlertId, again.AlertId);
        Assert.Equal(clock.Now, again.RaisedAt);
        Assert.Equal(2, (await RowsAsync(factory)).Count(row => row.Key == budgetKey));
    }

    [Fact]
    public async Task ANewValue_UpdatesTheAlert_AndKeepsItsIdAndStart()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        var clock = new TestClock(FinanceData.Now);
        await using var factory = Start(finance, clock);
        var client = factory.CreateClient();
        var first = Assert.Single((await GetAlertsAsync(client)).Alerts, alert => alert.Id == "money.unsorted");

        data.Answers["GET /api/Reports/uncategorised"] = new { merchants = Array.Empty<object>(), transactionCount = 3 };
        clock.Now = FinanceData.Now.AddMinutes(5);
        var updated = Assert.Single((await GetAlertsAsync(client, refresh: true)).Alerts, alert => alert.Id == "money.unsorted");

        Assert.Equal((first.AlertId, first.RaisedAt, clock.Now), (updated.AlertId, updated.RaisedAt, updated.UpdatedAt));
        Assert.Equal("{count} transactions are in Other because no rule names their merchants", updated.Text);
        Assert.Equal(AlertValue.Count(3), updated.Values["count"]);

        // No change: the alert stays as it is.
        clock.Now = FinanceData.Now.AddMinutes(10);
        var same = Assert.Single((await GetAlertsAsync(client, refresh: true)).Alerts, alert => alert.Id == "money.unsorted");
        Assert.Equal(FinanceData.Now.AddMinutes(5), same.UpdatedAt);
        Assert.Single(await RowsAsync(factory), row => row.Key == "money.unsorted");
    }

    [Fact]
    public async Task AReportThatDoesNotLoad_LeavesItsAlertsAsTheyAre_AndTheModuleIsPartial()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = Start(finance, new TestClock(FinanceData.Now));
        var client = factory.CreateClient();
        await GetAlertsAsync(client);

        data.Failures["GET /api/Accounts"] = 500;
        data.Answers["GET /api/Reports/uncategorised"] = new { merchants = Array.Empty<object>(), transactionCount = 0 };
        var list = await GetAlertsAsync(client, refresh: true);

        Assert.Equal(AlertModuleStatus.Partial, Assert.Single(list.Modules).Status);
        // The statement alerts need the accounts: they stay. The unsorted transactions are gone: resolved.
        Assert.Contains(list.Alerts, alert => alert.Id == "money.statements-not-imported");
        Assert.Contains(list.Alerts, alert => alert.Id == $"money.statements-behind.{FinanceData.TfsaId}");
        Assert.DoesNotContain(list.Alerts, alert => alert.Id == "money.unsorted");
    }

    [Fact]
    public async Task FinanceDown_AtTheFirstCheck_TheModuleFails_AndTheAnswerIsStill200()
    {
        var data = new FinanceData();
        foreach (var path in FinanceData.Paths)
        {
            data.Failures[path] = 502;
        }
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = Start(finance, new TestClock(FinanceData.Now));

        var list = await GetAlertsAsync(factory.CreateClient());

        Assert.Equal(new AlertModuleDto("Money", 0, AlertModuleStatus.Failed), Assert.Single(list.Modules));
        Assert.Empty(list.Alerts);
    }

    [Fact]
    public async Task BudgetLimitReached_RaisesItsAlertAtOnce_AndTheSameEventTwice_GivesOneAlert()
    {
        // Finance is down: only the event can raise the alert.
        var data = new FinanceData();
        foreach (var path in FinanceData.Paths)
        {
            data.Failures[path] = 502;
        }
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = Start(finance, new TestClock(FinanceData.Now));
        var client = factory.CreateClient();
        var budgetId = Guid.NewGuid();
        var limit = new BudgetLimitReached(budgetId, "Dining", BudgetLimitReached.LimitPercent, 500m, 520m,
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), FinanceData.Now);
        var warning = new BudgetLimitReached(Guid.NewGuid(), "Fuel", BudgetLimitReached.WarningPercent, 200m, 165m,
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), FinanceData.Now);

        await DeliverAsync(factory, limit);
        await DeliverAsync(factory, limit);
        await DeliverAsync(factory, warning);

        var rows = await RowsAsync(factory);
        var row = Assert.Single(rows);
        Assert.Equal(($"money.budget-over.{budgetId}", FinanceAlertSource.SourceName, (DateTimeOffset?)null), (row.Key, row.Source, row.ResolvedAt));
        Assert.Equal(1, await InboxRowsAsync(factory, limit.EventId, FinanceAlertsConsumer.InboxName));

        var list = await GetAlertsAsync(client);
        var alert = Assert.Single(list.Alerts);
        Assert.Equal(("{budget} is over its limit ({used} used)", AlertSeverity.Bad), (alert.Text, alert.Severity));
        Assert.Equal((AlertValue.Plain("Dining"), AlertValue.Percent(104m)), (alert.Values["budget"], alert.Values["used"]));
        // No check worked, but the module has an alert: Partial, not Failed.
        Assert.Equal(AlertModuleStatus.Partial, Assert.Single(list.Modules).Status);
    }

    [Fact]
    public async Task AFinanceEvent_AsksForACheck()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = Start(finance, new TestClock(FinanceData.Now));
        var client = factory.CreateClient();
        Assert.Contains((await GetAlertsAsync(client)).Alerts, alert => alert.Id == "money.unsorted");

        // A new rule sorts the last transaction, and a statement comes in.
        data.Answers["GET /api/Reports/uncategorised"] = new { merchants = Array.Empty<object>(), transactionCount = 0 };
        await DeliverAsync(factory, new TransactionsImported(FinanceData.ChequingId, "Chequing", 3, 3, 0,
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 4), FinanceData.Now));

        // No refresh: the stored alerts. The check after the event resolves the item.
        await EventuallyAsync(async () => (await GetAlertsAsync(client)).Alerts.All(alert => alert.Id != "money.unsorted"),
            "the check after TransactionsImported resolves money.unsorted");
        Assert.Equal(2 * FinanceData.AlertPaths.Length, FinanceData.SystemRequests(finance).Count);
    }

    [Fact]
    public async Task TheTimer_ChecksWithoutARequest_AndDeletesOldResolvedAlerts()
    {
        var data = new FinanceData();
        await using var finance = await FakeFinance.StartAsync(data.RespondAsync);
        await using var factory = Start(finance, new TestClock(FinanceData.Now), new()
        {
            ["Alerts:CheckInterval"] = "00:00:01",
            ["Alerts:KeepResolvedDays"] = "30"
        });
        // An alert that was resolved 31 days ago, and one 29 days ago.
        await factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<AlertsDbContext>();
            db.Alerts.Add(OldAlert("money.unsorted", resolvedDaysAgo: 31));
            db.Alerts.Add(OldAlert("money.budget-over.old", resolvedDaysAgo: 29));
            return await db.SaveChangesAsync();
        });

        await EventuallyAsync(async () => (await RowsAsync(factory)).Count(row => row.ResolvedAt is null) == MoneyKeys.Length,
            "the timer raises the finance alerts");
        await EventuallyAsync(async () => (await RowsAsync(factory)).All(row => row.ResolvedAt is null || row.Key == "money.budget-over.old"),
            "the timer deletes the alert that was resolved 31 days ago");
    }

    [Fact]
    public async Task TheOpenApiDocument_DescribesTheAlertsEndpoint()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = new HestiaApiFactory(finance.HostSettings());

        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var get = document.RootElement.GetProperty("paths").GetProperty("/api/alerts").GetProperty("get");
        Assert.Equal("GetAlerts", get.GetProperty("operationId").GetString());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("AlertList", out _));
        Assert.True(schemas.TryGetProperty("AlertDto", out _));
    }

    [Fact]
    public async Task AnUnknownTimeZone_StopsTheHost()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = Start(finance, settings: new() { ["Alerts:TimeZone"] = "Mars/Olympus" });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Alerts:TimeZone", error.ToString());
    }

    private static void SetBudgets(FinanceData data, params object[] budgets) =>
        data.Answers["POST /api/Budget/Filter"] = new { items = budgets, page = 1, pageSize = 1000, totalCount = budgets.Length };

    private static Alert OldAlert(string key, int resolvedDaysAgo) => new()
    {
        Id = Guid.CreateVersion7(),
        Source = FinanceAlertSource.SourceName,
        Module = "Money",
        Key = key,
        Kind = "old",
        Severity = AlertSeverity.Warning,
        Title = "Old",
        Text = "Old",
        SortKey = "9",
        RaisedAt = FinanceData.Now.AddDays(-resolvedDaysAgo - 1),
        UpdatedAt = FinanceData.Now.AddDays(-resolvedDaysAgo - 1),
        ResolvedAt = FinanceData.Now.AddDays(-resolvedDaysAgo)
    };
}
