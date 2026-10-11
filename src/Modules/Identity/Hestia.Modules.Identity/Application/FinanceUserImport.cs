using System.Text.Json;
using Hestia.Platform.Modules;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Identity.Application;

/// <summary>
/// One-time command (T03): copies the accounts of the finance backend, with their password hashes, into the Identity
/// module. The user signs in to Hestia with the same password as before. Current sessions end; the user signs in
/// again once.
/// <para>
/// Input: one JSON object per line on standard input, with the columns of the finance table "AspNetUsers"
/// (at least "UserName" and "PasswordHash"). The README shows the psql command that writes them.
/// </para>
/// <para>
/// A new account keeps its finance id. An account that exists gets the finance password hash. Both get a new security
/// stamp and no lockout. It is safe to run the command again.
/// </para>
/// </summary>
public sealed class FinanceUserImport : IModuleCommand
{
    public const string CommandName = "import-finance-user";

    public string Name => CommandName;

    public string Description =>
        "Copies the finance accounts (JSON lines from psql on standard input) into Hestia, with their password hashes.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The columns that the command reads. Other columns are ignored.</summary>
    private sealed record FinanceUser(string? Id, string? UserName, string? PasswordHash);

    public async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> arguments, TextReader input, TextWriter output,
        CancellationToken cancellationToken)
    {
        var accounts = new List<FinanceUser>();
        var lineNumber = 0;
        while (await input.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            FinanceUser? account;
            try
            {
                account = JsonSerializer.Deserialize<FinanceUser>(line, Json);
            }
            catch (JsonException)
            {
                account = null;
            }
            if (account is not { UserName.Length: > 0, PasswordHash.Length: > 0 })
            {
                await output.WriteLineAsync($"Line {lineNumber} is not a finance account (JSON with \"UserName\" and \"PasswordHash\"). Nothing was imported.");
                return 1;
            }
            accounts.Add(account);
        }

        if (accounts.Count == 0)
        {
            await output.WriteLineAsync("No accounts on standard input. Nothing was imported. See the README, section \"Sign-in\".");
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        foreach (var account in accounts)
        {
            var user = await users.FindByNameAsync(account.UserName!);
            var isNew = user is null;
            if (user is null)
            {
                user = new IdentityUser(account.UserName!);
                if (!string.IsNullOrEmpty(account.Id))
                {
                    user.Id = account.Id;
                }
                user.PasswordHash = account.PasswordHash;
                // CreateAsync without a password keeps the hash that is on the user.
                if (!await CheckAsync(await users.CreateAsync(user), account, output))
                {
                    return 1;
                }
            }
            else
            {
                user.PasswordHash = account.PasswordHash;
                if (!await CheckAsync(await users.UpdateAsync(user), account, output))
                {
                    return 1;
                }
                // A new stamp ends the sessions of the account (with the old password).
                await users.UpdateSecurityStampAsync(user);
            }
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            await output.WriteLineAsync(isNew
                ? $"Imported the account {user.UserName}."
                : $"Updated the password of the account {user.UserName} from finance.");
        }
        return 0;
    }

    private static async Task<bool> CheckAsync(IdentityResult result, FinanceUser account, TextWriter output)
    {
        if (result.Succeeded)
        {
            return true;
        }
        await output.WriteLineAsync(
            $"Could not import {account.UserName}: {string.Join(" ", result.Errors.Select(error => error.Description))}");
        return false;
    }
}
