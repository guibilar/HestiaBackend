using System.Security.Claims;
using System.Text.Encodings.Web;
using Hestia.Platform.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hestia.Testing;

/// <summary>
/// Signs in every request as one user with full access, so module tests do not have to sign in.
/// <see cref="HestiaApiFactory"/> uses it unless <c>signedIn</c> is false; then the real sign-in (the Identity module)
/// is in charge. The header <see cref="ReadOnlyHeader"/> makes the request a read-only user (the read-only token).
/// </summary>
public class TestSignIn(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserId = "test-user-id";
    public const string Username = "test-user";

    /// <summary>A request with this header (any value) is a read-only user.</summary>
    public const string ReadOnlyHeader = "X-Test-Read-Only";

    public static void Register(IServiceCollection services)
    {
        services.AddAuthentication(options =>
            {
                options.DefaultScheme = SchemeName;
                options.DefaultAuthenticateScheme = SchemeName;
                options.DefaultChallengeScheme = SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, TestSignIn>(SchemeName, _ => { });
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, UserId),
            new(ClaimTypes.Name, Username)
        };
        if (Request.Headers.ContainsKey(ReadOnlyHeader))
        {
            claims.Add(new Claim(HestiaUser.ScopeClaim, HestiaUser.ReadOnlyScope));
        }
        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
