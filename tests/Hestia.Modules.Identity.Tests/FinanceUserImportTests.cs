using System.Net;
using System.Text.Json;
using Hestia.Platform.Modules;
using Hestia.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static Hestia.Modules.Identity.Tests.SignInHost;

namespace Hestia.Modules.Identity.Tests;

/// <summary>
/// The one-time command <c>import-finance-user</c> (T03): the finance account, with its password hash, moves to the
/// Identity module. The user signs in to Hestia with the same password.
/// </summary>
public class FinanceUserImportTests
{
    private const string FinanceId = "6f1c0d3e-2b8a-4c55-9e0a-1d2c3b4a5f60";

    /// <summary>One line as psql writes it from the finance table "AspNetUsers" (see the README).</summary>
    private static string FinanceRow(string password, string username = Username) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Id"] = FinanceId,
            ["UserName"] = username,
            ["NormalizedUserName"] = username.ToUpperInvariant(),
            ["PasswordHash"] = new PasswordHasher<IdentityUser>().HashPassword(new IdentityUser(username), password),
            ["SecurityStamp"] = "FINANCESTAMP",
            ["LockoutEnabled"] = true,
            ["AccessFailedCount"] = 0
        });

    private static async Task<(int ExitCode, string Output)> RunAsync(HestiaApiFactory factory, string input)
    {
        _ = factory.CreateClient();
        var command = factory.Services.GetServices<IModuleCommand>().Single(c => c.Name == "import-finance-user");
        var output = new StringWriter();
        var exitCode = await command.RunAsync(factory.Services, [], new StringReader(input), output, CancellationToken.None);
        return (exitCode, output.ToString());
    }

    [Fact]
    public async Task Import_MakesTheAccount_WithTheFinancePassword_AndId()
    {
        await using var factory = new HestiaApiFactory(signedIn: false);

        var (exitCode, output) = await RunAsync(factory, FinanceRow(Password) + "\n");

        Assert.Equal(0, exitCode);
        Assert.Contains($"Imported the account {Username}", output);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(factory.CreateClient())).StatusCode);
        var id = await factory.WithScopeAsync(async provider =>
            (await provider.GetRequiredService<UserManager<IdentityUser>>().FindByNameAsync(Username))!.Id);
        Assert.Equal(FinanceId, id);
    }

    [Fact]
    public async Task Import_AgainAfterAPasswordChangeInFinance_SetsTheNewPassword()
    {
        await using var factory = await WithAccountAsync();

        var (exitCode, output) = await RunAsync(factory, FinanceRow("the newer finance password"));

        Assert.Equal(0, exitCode);
        Assert.Contains("Updated the password", output);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(factory.CreateClient())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(factory.CreateClient(), "the newer finance password")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"UserName\":\"alex\"}")]
    public async Task Import_WithoutAValidAccount_ImportsNothing_AndFails(string input)
    {
        await using var factory = new HestiaApiFactory(signedIn: false);

        var (exitCode, _) = await RunAsync(factory, input);

        Assert.Equal(1, exitCode);
        var count = await factory.WithScopeAsync(provider =>
            Task.FromResult(provider.GetRequiredService<UserManager<IdentityUser>>().Users.Count()));
        Assert.Equal(0, count);
    }
}
