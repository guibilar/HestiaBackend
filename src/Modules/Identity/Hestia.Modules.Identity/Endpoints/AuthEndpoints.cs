using System.Security.Claims;
using Hestia.Modules.Identity.Application;
using Hestia.Modules.Identity.Contracts;
using Hestia.Platform.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;

namespace Hestia.Modules.Identity.Endpoints;

/// <summary>
/// The sign-in endpoints. The path (<c>/api/Auth</c>), the cookie, the bodies, and the status codes are the same as in
/// the finance backend before T03, so the clients do not change (D5). This is the one module path that does not use
/// the module name as its prefix.
/// </summary>
public static class AuthEndpoints
{
    public const string Prefix = "/api/Auth";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup(Prefix);

        // Signs in and sets the session cookie.
        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(SignInGuard.PolicyName);

        // Ends the session (on the server too: a copy of the cookie stops working). No error when already signed out.
        auth.MapPost("/logout", async (SignInManager<IdentityUser> signIn) =>
            {
                await signIn.SignOutAsync();
                return Results.NoContent();
            })
            .AllowAnonymous();

        // Who is signed in, and the scope (the same rule as the authorization, HestiaUser.ScopeOf); 401 when nobody is.
        auth.MapGet("/me", (ClaimsPrincipal user) => new CurrentUserDto(user.Identity!.Name!, HestiaUser.ScopeOf(user)));

        auth.MapPost("/change-password", ChangePasswordAsync)
            .RequireRateLimiting(SignInGuard.PolicyName);

        // Ends every other session of this account (other browsers and devices). This session stays signed in.
        auth.MapPost("/sign-out-others", async (ClaimsPrincipal user, DbTicketStore sessions) =>
            new SignedOutSessionsDto(await sessions.RemoveOtherSessionsAsync(user)));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest login, UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn, UnknownUsernames unknownUsernames)
    {
        if (string.IsNullOrEmpty(login.Username) || string.IsNullOrEmpty(login.Password))
        {
            return Results.ValidationProblem(Required(
                (nameof(LoginRequest.Username), login.Username), (nameof(LoginRequest.Password), login.Password)));
        }

        var user = await users.FindByNameAsync(login.Username);
        if (user is null)
        {
            // The same answer as a wrong password, as slow, and a lockout after as many tries: no answer tells which
            // user names exist.
            return unknownUsernames.Fail(users, login.Username, login.Password) ? LockedOut() : SignInFailed();
        }
        var result = await signIn.PasswordSignInAsync(user, login.Password, login.RememberMe, lockoutOnFailure: true);
        // User names match without case; answer with the stored spelling.
        if (result.Succeeded)
        {
            // A password sign-in (the cookie) always has the full scope.
            return Results.Ok(new CurrentUserDto(user.UserName!, HestiaUser.FullScope));
        }
        return result.IsLockedOut ? LockedOut() : SignInFailed();
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest change, ClaimsPrincipal principal, UserManager<IdentityUser> users,
        SignInManager<IdentityUser> signIn, DbTicketStore sessions)
    {
        if (string.IsNullOrEmpty(change.CurrentPassword) || string.IsNullOrEmpty(change.NewPassword))
        {
            return Results.ValidationProblem(Required(
                (nameof(ChangePasswordRequest.CurrentPassword), change.CurrentPassword),
                (nameof(ChangePasswordRequest.NewPassword), change.NewPassword)));
        }

        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        // A wrong current password counts towards the lockout, as at sign-in. Else a session that stays open on a
        // device would let somebody guess the password without a limit.
        var check = await signIn.CheckPasswordSignInAsync(user, change.CurrentPassword, lockoutOnFailure: true);
        if (!check.Succeeded)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(ChangePasswordRequest.CurrentPassword)] =
                [
                    check.IsLockedOut ? "Too many wrong passwords. Try again in 15 minutes." : "Incorrect password."
                ]
            });
        }

        var result = await users.ChangePasswordAsync(user, change.CurrentPassword, change.NewPassword);
        if (!result.Succeeded)
        {
            var errors = result.Errors
                .GroupBy(error => error.Code == "PasswordMismatch"
                    ? nameof(ChangePasswordRequest.CurrentPassword)
                    : nameof(ChangePasswordRequest.NewPassword))
                .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray());
            return Results.ValidationProblem(errors);
        }

        // All other sessions end (they can use the old password). This session continues.
        await sessions.RemoveOtherSessionsAsync(principal);
        await signIn.RefreshSignInAsync(user);
        return Results.NoContent();
    }

    private static IResult LockedOut() => Results.Problem(statusCode: StatusCodes.Status429TooManyRequests,
        title: "Too many attempts", detail: "Too many failed sign-in attempts. Try again in 15 minutes.");

    private static IResult SignInFailed() => Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
        title: "Sign-in failed", detail: "Incorrect username or password.");

    private static Dictionary<string, string[]> Required(params (string Field, string? Value)[] fields) =>
        fields.Where(field => string.IsNullOrEmpty(field.Value))
            .ToDictionary(field => field.Field, field => new[] { $"The {field.Field} field is required." });
}
