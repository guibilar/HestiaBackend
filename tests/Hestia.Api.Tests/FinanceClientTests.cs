using System.Net;
using System.Security.Claims;
using Hestia.Platform.Finance;
using Hestia.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Api.Tests;

/// <summary>
/// The shared finance client (D13, T03): modules call finance directly at FinanceProxy:Address, with the gateway token
/// of the signed-in user of the current request.
/// </summary>
public class FinanceClientTests
{
    private sealed record FinanceAnswer(string Source);

    private static ClaimsPrincipal User(bool readOnly = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "u1"), new(ClaimTypes.Name, "alex") };
        if (readOnly)
        {
            claims.Add(new Claim(Hestia.Platform.Auth.HestiaUser.ScopeClaim, Hestia.Platform.Auth.HestiaUser.ReadOnlyScope));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    /// <summary>The finance client of a host, inside a "request" of <paramref name="user"/>.</summary>
    private static (IFinanceClient Client, AsyncServiceScope Scope) ClientFor(HestiaApiFactory host, ClaimsPrincipal? user)
    {
        _ = host.CreateClient();
        var scope = host.Services.CreateAsyncScope();
        var requests = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        requests.HttpContext = user is null ? null : new DefaultHttpContext { User = user };
        return (scope.ServiceProvider.GetRequiredService<IFinanceClient>(), scope);
    }

    [Fact]
    public async Task Get_CallsFinanceDirectly_WithTheTokenOfTheUser()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = new HestiaApiFactory(finance.HostSettings());
        var (client, scope) = ClientFor(host, User());
        await using var _ = scope;

        var answer = await client.GetAsync<FinanceAnswer>("/api/Reports/summary?month=2026-10");

        Assert.Equal("finance", answer.Source);
        var received = Assert.Single(finance.Requests);
        Assert.Equal("GET /api/Reports/summary?month=2026-10", $"{received.Method} {received.Target}");
        // Directly, not through the host's proxy: no forwarded headers.
        Assert.Null(received.Header("X-Forwarded-For"));
        var token = GatewayToken.Read(received);
        Assert.Equal(("u1", "alex", "full"), (token.Subject, token.Name, token.Scope));
    }

    [Fact]
    public async Task Post_SendsJson_AndAReadOnlyUser_GetsTheReadOnlyScope()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = new HestiaApiFactory(finance.HostSettings());
        var (client, scope) = ClientFor(host, User(readOnly: true));
        await using var _ = scope;

        var answer = await client.PostAsync<object, FinanceAnswer>("/api/Transaction/Filter", new { PageSize = 5 });

        Assert.Equal("finance", answer.Source);
        var received = Assert.Single(finance.Requests);
        Assert.Equal("{\"pageSize\":5}", received.Body);
        Assert.Equal("read-only", GatewayToken.Read(received).Scope);
    }

    [Fact]
    public async Task AnErrorStatus_IsAFinanceRequestException_WithTheStatus()
    {
        await using var finance = await FakeFinance.StartAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });
        await using var host = new HestiaApiFactory(finance.HostSettings());
        var (client, scope) = ClientFor(host, User());
        await using var _ = scope;

        var error = await Assert.ThrowsAsync<FinanceRequestException>(() => client.GetAsync<FinanceAnswer>("/api/Accounts"));

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
    }

    [Fact]
    public async Task FinanceDown_IsAFinanceRequestException_WithoutAStatus()
    {
        // Nothing listens on port 9 (discard), so the connection is refused.
        await using var host = new HestiaApiFactory(new Dictionary<string, string?> { ["FinanceProxy:Address"] = "http://127.0.0.1:9" });
        var (client, scope) = ClientFor(host, User());
        await using var _ = scope;

        var error = await Assert.ThrowsAsync<FinanceRequestException>(() => client.GetAsync<FinanceAnswer>("/api/Accounts"));

        Assert.Null(error.StatusCode);
    }

    [Fact]
    public async Task WithoutASignedInUser_TheClientRefuses()
    {
        await using var finance = await FakeFinance.StartAsync();
        await using var host = new HestiaApiFactory(finance.HostSettings());
        var (client, scope) = ClientFor(host, null);
        await using var _ = scope;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync<FinanceAnswer>("/api/Accounts"));
        Assert.Empty(finance.Requests);
    }
}
