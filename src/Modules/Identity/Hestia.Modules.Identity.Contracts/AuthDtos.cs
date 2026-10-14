namespace Hestia.Modules.Identity.Contracts;

// The shapes of the /api/Auth endpoints. They are the same as in the finance backend before T03, so the clients
// (web and Android) do not change.

/// <summary>The body of <c>POST /api/Auth/login</c>.</summary>
/// <param name="RememberMe">Keep the session after the browser closes (for up to 14 days of inactivity).</param>
public sealed record LoginRequest(string? Username, string? Password, bool RememberMe = false);

/// <summary>The body of <c>POST /api/Auth/change-password</c>.</summary>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>The signed-in user (<c>GET /api/Auth/me</c>, and the answer to a sign-in).</summary>
/// <param name="Scope">
/// <c>full</c> or <c>read-only</c> (T60). A read-only user can call GET and the read-only queries only, so the clients
/// hide their write actions. A cookie user is always <c>full</c>. Old clients ignore this field.
/// </param>
public sealed record CurrentUserDto(string Username, string Scope);

/// <summary>The answer to <c>POST /api/Auth/sign-out-others</c>.</summary>
/// <param name="Count">How many other sessions (browsers, devices) were signed out.</param>
public sealed record SignedOutSessionsDto(int Count);
