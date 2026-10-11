using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Identity.Application;

/// <summary>
/// Makes (or, when asked, resets) the account in <c>Identity:InitialUser</c> when the host starts. It runs after the
/// migrations. Without this setting, it only warns when no account exists. (Moved from the finance backend in T03.)
/// </summary>
public sealed class InitialUser(IServiceScopeFactory scopes, IOptions<IdentityModuleOptions> options, ILogger<InitialUser> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var initial = options.Value.InitialUser;
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        if (string.IsNullOrWhiteSpace(initial.Username))
        {
            await WarnIfNoAccountsAsync(users, cancellationToken);
            return;
        }
        if (string.IsNullOrEmpty(initial.Password))
        {
            throw new InvalidOperationException("Identity:InitialUser:Password must be set together with Identity:InitialUser:Username.");
        }

        var user = await users.FindByNameAsync(initial.Username);
        if (user is null)
        {
            Check(await users.CreateAsync(new IdentityUser(initial.Username), initial.Password), "make the initial user");
            logger.LogInformation("Made the account {Username}", initial.Username);
        }
        else if (initial.ResetPassword)
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            Check(await users.ResetPasswordAsync(user, token, initial.Password), "reset the password");
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            logger.LogWarning("Reset the password of {Username}. Remove Identity:InitialUser:ResetPassword now", initial.Username);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WarnIfNoAccountsAsync(UserManager<IdentityUser> users, CancellationToken cancellationToken)
    {
        try
        {
            if (!await users.Users.AnyAsync(cancellationToken))
            {
                logger.LogWarning("No accounts exist, so nobody can sign in. Import the finance account " +
                                  "(import-finance-user), or set Identity__InitialUser__Username and Identity__InitialUser__Password.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only a hint: the database can be without migrations (Database:MigrateOnStartup off).
            logger.LogDebug(ex, "Could not look for accounts");
        }
    }

    private static void Check(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not {action} (Identity:InitialUser): {string.Join(" ", result.Errors.Select(e => e.Description))}");
        }
    }
}
