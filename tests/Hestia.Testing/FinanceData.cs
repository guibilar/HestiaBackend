using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Hestia.Testing;

/// <summary>
/// The finance answers for the Today and Alerts tests, served by <see cref="FakeFinance"/>: one JSON body per finance
/// path, in the shapes of Finance.Contracts. The day of the data is <see cref="Today"/> (September 5th, 2026,
/// America/Toronto), the same as in the web dashboard tests (attention.spec.ts).
/// </summary>
public sealed class FinanceData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 5, 16, 0, 0, TimeSpan.Zero);
    public static readonly DateOnly Today = new(2026, 9, 5);
    public const string TimeZone = "America/Toronto";

    public static readonly Guid GroceriesId = Guid.Parse("0199a1a1-0000-7000-8000-000000000001");
    public static readonly Guid FuelId = Guid.Parse("0199a1a1-0000-7000-8000-000000000002");
    public static readonly Guid TfsaId = Guid.Parse("0199a1a1-0000-7000-8000-000000000003");
    public static readonly Guid ChequingId = Guid.Parse("0199a1a1-0000-7000-8000-000000000004");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The answer for each path (method and path, without the query string).</summary>
    public ConcurrentDictionary<string, object> Answers { get; } = new(StringComparer.Ordinal)
    {
        ["GET /api/Reports/current-month-categorized-graph"] = new
        {
            categories = Array.Empty<object>(),
            monthlyData = new[] { new { month = "Sep 2026", total = 400m, categoryValues = new { Groceries = 400m } } },
            averageMonthlyTotal = 400m,
            averagedMonths = 1,
            averageIncludesCurrentMonth = true,
            previousMonthToDateTotal = 500m
        },
        ["GET /api/Reports/monthly-categorized-graph"] = new
        {
            categories = Array.Empty<object>(),
            monthlyData = new[]
            {
                new { month = "Aug 2026", total = 3000m, categoryValues = new { Groceries = 3000m } },
                new { month = "Sep 2026", total = 400m, categoryValues = new { Groceries = 400m } }
            },
            averageMonthlyTotal = 3000m,
            averagedMonths = 8,
            averageIncludesCurrentMonth = false,
            previousMonthToDateTotal = (decimal?)null
        },
        ["GET /api/Reports/cash-flow"] = new
        {
            months = new[]
            {
                new { month = "Aug 2026", income = 4000m, spending = 3000m, net = 1000m, savingsChange = 0m },
                new { month = "Sep 2026", income = 2000m, spending = 400m, net = 1600m, savingsChange = 0m }
            },
            averageMonthlyIncome = 4000m,
            averageMonthlySpending = 3000m,
            averageMonthlyNet = 1000m,
            averageMonthlySavingsChange = 0m,
            averagedMonths = 8,
            averagesIncludeCurrentMonth = false
        },
        ["GET /api/Reports/net-worth"] = new
        {
            assets = 11000m,
            liabilities = 500m,
            netWorth = 10500m,
            accounts = Array.Empty<object>(),
            months = new[]
            {
                new { month = "Aug 2026", date = "2026-08-31T00:00:00Z", assets = 10500m, liabilities = 500m, netWorth = 10000m, accountsCounted = 2 },
                new { month = "Sep 2026", date = "2026-09-05T00:00:00Z", assets = 11000m, liabilities = 500m, netWorth = 10500m, accountsCounted = 2 }
            }
        },
        ["POST /api/Budget/Filter"] = new
        {
            items = new[]
            {
                BudgetJson(GroceriesId, "Groceries", 150m, 180m, 120m),
                BudgetJson(FuelId, "Fuel", 200m, 100m, 50m)
            },
            page = 1,
            pageSize = 1000,
            totalCount = 2
        },
        ["GET /api/Reports/uncategorised"] = new { merchants = Array.Empty<object>(), transactionCount = 1 },
        ["GET /api/Reports/recurring"] = new
        {
            payments = new[]
            {
                PaymentJson("NETFLIX", "2026-08-20T00:00:00Z"),
                // Too long ago to mention.
                PaymentJson("OLD GYM", "2026-03-20T00:00:00Z"),
                // Stopped: no mention.
                PaymentJson("STOPPED", "2026-08-25T00:00:00Z", "Stopped")
            },
            monthlyTotal = 18.99m,
            yearlyTotal = 227.88m
        },
        ["GET /api/Accounts"] = new object[]
        {
            // A quiet Wealthsimple account: its statements reach June 30th only.
            new
            {
                id = TfsaId, name = "TFSA", type = "Investment", format = "Wealthsimple", transactionCount = 0,
                lastTransactionDate = (string?)null, lastImportedAt = (string?)null, plan = "Tfsa",
                balance = new { id = Guid.NewGuid(), date = "2026-06-30T00:00:00Z", amount = 100m }
            },
            // Imported on August 27th (11:00 in Toronto): 9 days before September 5th.
            new
            {
                id = ChequingId, name = "Chequing", type = "Chequing", format = "Cibc", transactionCount = 3,
                lastTransactionDate = "2026-08-26T00:00:00Z", lastImportedAt = "2026-08-27T15:00:00Z", plan = "None",
                balance = (object?)null
            }
        }
    };

    /// <summary>Paths that answer with this status code and no body.</summary>
    public ConcurrentDictionary<string, int> Failures { get; } = new(StringComparer.Ordinal);

    public static object BudgetJson(Guid id, string name, decimal amount, decimal spent, decimal used) => new
    {
        id, name, description = "", status = "Active", period = "Monthly", categories = Array.Empty<object>(),
        totalAmount = amount, totalSpent = spent, totalRemaining = amount - spent, percentageUsed = used
    };

    public static object PaymentJson(string name, string since, string status = "Active") => new
    {
        name, cadence = "Monthly", status, amount = 18.99m, variableAmount = false, firstDate = "2025-01-20T00:00:00Z",
        lastDate = since, lastAmount = 18.99m, nextDate = "2026-09-20T00:00:00Z", count = 20, monthlyCost = 18.99m,
        yearlyCost = 227.88m, category = (object?)null,
        priceChange = new { from = 16.49m, to = 18.99m, since }
    };

    /// <summary>The <see cref="FakeFinance.Respond"/> function: the answer of the path, or 404.</summary>
    public async Task RespondAsync(HttpContext context)
    {
        if (context.Request.Path == "/health/ready")
        {
            await context.Response.WriteAsync("Healthy");
            return;
        }
        var key = $"{context.Request.Method} {context.Request.Path}";
        if (Failures.TryGetValue(key, out var status))
        {
            context.Response.StatusCode = status;
            return;
        }
        if (!Answers.TryGetValue(key, out var answer))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(answer, Json));
    }

    /// <summary>The finance paths that the Today tiles read, as the signed-in user.</summary>
    public static readonly string[] TilePaths =
    [
        "GET /api/Reports/current-month-categorized-graph",
        "GET /api/Reports/monthly-categorized-graph",
        "GET /api/Reports/cash-flow",
        "GET /api/Reports/net-worth",
        "POST /api/Budget/Filter"
    ];

    /// <summary>The finance paths that the finance alert rules read, as the system identity (T15).</summary>
    public static readonly string[] AlertPaths =
    [
        "POST /api/Budget/Filter",
        "GET /api/Reports/uncategorised",
        "GET /api/Reports/recurring",
        "GET /api/Accounts"
    ];

    /// <summary>All finance paths of the Today screen: the tiles and the alerts.</summary>
    public static readonly string[] Paths = TilePaths.Union(AlertPaths).ToArray();

    /// <summary>The subject of the system identity in the gateway token (T42).</summary>
    public const string SystemSubject = "hestia-system";

    /// <summary>The finance requests of background code (the system identity, T42), for example the alert checks.</summary>
    public static IReadOnlyList<FinanceRequest> SystemRequests(FakeFinance finance) =>
        finance.Requests.Where(request => GatewayToken.SubjectOf(request) == SystemSubject).ToList();

    /// <summary>The finance requests of signed-in users.</summary>
    public static IReadOnlyList<FinanceRequest> UserRequests(FakeFinance finance) =>
        finance.Requests.Where(request => GatewayToken.SubjectOf(request) != SystemSubject).ToList();
}
