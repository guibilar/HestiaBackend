using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Hestia.Platform.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Modules.Identity.Application;

/// <summary>
/// A token for scripts and assistants that only read: <c>Authorization: Bearer &lt;Identity:ReadOnlyToken&gt;</c>.
/// The user that it signs in has the read-only scope (<see cref="HestiaUser.ReadOnlyScope"/>). It can call every GET
/// and the endpoints marked as read-only queries; anything else answers 403 (<see cref="HestiaUser.MayProceed"/>).
/// Finance gets the scope in the gateway token, and checks its own read-only queries again. Off until
/// Identity:ReadOnlyToken is set. (Moved from the finance backend in T03.)
/// </summary>
public class ReadOnlyTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IOptionsMonitor<IdentityModuleOptions> settings)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ReadOnlyToken";
    public const string Username = "read-only token";
    public const string UserId = "read-only-token";
    private const string BearerPrefix = "Bearer ";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!HasBearerToken(Request))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string header = Request.Headers.Authorization!;
        var expected = settings.CurrentValue.ReadOnlyToken;
        if (string.IsNullOrEmpty(expected) || !SameToken(header[BearerPrefix.Length..].Trim(), expected.Trim()))
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown token."));
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, UserId),
            new Claim(ClaimTypes.Name, Username),
            new Claim(HestiaUser.ScopeClaim, HestiaUser.ReadOnlyScope)
        ], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    public static bool HasBearerToken(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase);

    // The hashes have the same length, so the comparison takes the same time for each guess.
    private static bool SameToken(string given, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
