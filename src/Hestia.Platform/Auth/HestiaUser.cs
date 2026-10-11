using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Hestia.Platform.Auth;

/// <summary>
/// The signed-in user of the host (D5). The Identity module signs users in; every module gets the normal
/// ASP.NET Core user. A user is "full" (the session cookie) or "read-only" (the read-only token).
/// </summary>
public static class HestiaUser
{
    /// <summary>The name of the session cookie. The clients know it, so it stays the same as in finance (D5).</summary>
    public const string SessionCookieName = "finance_session";

    /// <summary>The claim that marks a read-only user. A user without it has full access.</summary>
    public const string ScopeClaim = "hestia:scope";

    public const string FullScope = "full";
    public const string ReadOnlyScope = "read-only";

    /// <summary>
    /// True when the user is signed in only with read-only identities. Such a user can call GET, HEAD, and the
    /// endpoints marked with <see cref="ReadOnlyQueryAttribute"/>.
    /// </summary>
    public static bool IsReadOnly(ClaimsPrincipal user)
    {
        var signedIn = user.Identities.Where(identity => identity.IsAuthenticated).ToList();
        return signedIn.Count > 0 && signedIn.All(identity => identity.HasClaim(ScopeClaim, ReadOnlyScope));
    }

    /// <summary>The scope that the host gives to finance in the gateway token.</summary>
    public static string ScopeOf(ClaimsPrincipal user) => IsReadOnly(user) ? ReadOnlyScope : FullScope;

    /// <summary>
    /// The authorization rule of every endpoint (the fallback policy adds it): a full user can do everything, and a
    /// read-only user can only read.
    /// </summary>
    public static bool MayProceed(AuthorizationHandlerContext context)
    {
        if (!IsReadOnly(context.User))
        {
            return true;
        }
        return context.Resource is HttpContext http && IsReadRequest(http);
    }

    /// <summary>GET, HEAD, or an endpoint with <see cref="ReadOnlyQueryAttribute"/>.</summary>
    public static bool IsReadRequest(HttpContext http) =>
        HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) ||
        http.GetEndpoint()?.Metadata.GetMetadata<ReadOnlyQueryAttribute>() is not null;
}
