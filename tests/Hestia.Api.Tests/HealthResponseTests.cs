using System.Net;
using System.Text.Json;
using Hestia.Platform.Health;
using Hestia.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hestia.Api.Tests;

/// <summary>
/// T64: <c>/health/ready</c> answers JSON with the overall status and the name and status of each check. The endpoint
/// is open, so the answer has no descriptions, exception messages, addresses, or paths.
/// </summary>
public class HealthResponseTests
{
    [Fact]
    public async Task Ready_AnswersJson_WithEachCheck_WhenAllAreHealthy()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var factory = new HestiaApiFactory(finance.HostSettings());

        var response = await factory.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
        var checks = Checks(body);
        // The checks of the host and of the Documents module, in name order.
        var names = body.RootElement.GetProperty("checks").EnumerateArray().Select(check => check.GetProperty("name").GetString()!).ToList();
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Contains("database", checks.Keys);
        Assert.Contains("finance", checks.Keys);
        Assert.Contains("documents-storage", checks.Keys);
        Assert.All(checks.Values, status => Assert.Equal("Healthy", status));
    }

    [Fact]
    public async Task Ready_Is503_AndNamesTheFailedCheck_WithoutItsMessage()
    {
        const string address = "http://127.0.0.1:9";
        await using var factory = new HestiaApiFactory(new Dictionary<string, string?> { ["FinanceProxy:Address"] = address });

        var response = await factory.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        Assert.Equal("Unhealthy", body.RootElement.GetProperty("status").GetString());
        var checks = Checks(body);
        Assert.Equal("Unhealthy", checks["finance"]);
        Assert.Equal("Healthy", checks["database"]);
        // Only "status" and "checks", and only "name" and "status" in each check.
        Assert.Equal(["status", "checks"], body.RootElement.EnumerateObject().Select(p => p.Name).ToList());
        Assert.All(body.RootElement.GetProperty("checks").EnumerateArray(),
            check => Assert.Equal(["name", "status"], check.EnumerateObject().Select(p => p.Name).ToList()));
        Assert.DoesNotContain("127.0.0.1", text);
        Assert.DoesNotContain("answer", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheWriter_LeavesOutDescriptions_Exceptions_AndData()
    {
        var report = new HealthReport(new Dictionary<string, HealthReportEntry>
        {
            ["zeta"] = new(HealthStatus.Degraded, "The secret folder /data/secret is slow.", TimeSpan.FromMilliseconds(5),
                new IOException("Disk /dev/sda1 failed"), new Dictionary<string, object> { ["path"] = "/data/secret" }),
            ["alpha"] = new(HealthStatus.Healthy, null, TimeSpan.FromMilliseconds(1), null, null)
        }, TimeSpan.FromMilliseconds(6));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await HealthResponse.WriteAsync(context, report);

        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Equal("""{"status":"Degraded","checks":[{"name":"alpha","status":"Healthy"},{"name":"zeta","status":"Degraded"}]}""", text);
        Assert.Equal(HealthResponse.ContentType, context.Response.ContentType);
    }

    [Fact]
    public async Task Live_StillAnswers200()
    {
        await using var factory = new HestiaApiFactory(new Dictionary<string, string?> { ["FinanceProxy:Address"] = "http://127.0.0.1:9" });

        var response = await factory.CreateClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Dictionary<string, string> Checks(JsonDocument body) =>
        body.RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(check => check.GetProperty("name").GetString()!, check => check.GetProperty("status").GetString()!);
}
