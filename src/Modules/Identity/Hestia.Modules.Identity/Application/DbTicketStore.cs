using System.Security.Claims;
using System.Security.Cryptography;
using Hestia.Modules.Identity.Domain;
using Hestia.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hestia.Modules.Identity.Application;

/// <summary>
/// Keeps sign-ins in the database (<see cref="UserSession"/>), not in the cookie, so they can end: sign-out deletes
/// the session, and a copy of the cookie stops working with it. A singleton, as the cookie handler expects; each call
/// uses its own DbContext. (Moved from the finance backend in T03.)
/// </summary>
public class DbTicketStore(IServiceScopeFactory scopes, TimeProvider timeProvider) : ITicketStore
{
    /// <summary>
    /// The session key on the signed-in user. The cookie handler reads its own copy from the cookie and then works with
    /// the ticket stored here, so the store adds the key when it gives a ticket back.
    /// </summary>
    internal const string SessionKeyClaim = "hestia:session";

    /// <summary>Where a sign-in request keeps the key of the session it came with (see <see cref="StartsNewSession"/>).</summary>
    private const string PreviousSessionItem = "hestia:previous-session";

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var now = timeProvider.GetUtcNow();
        var session = new UserSession
        {
            // The cookie is encrypted anyway. A random key keeps sessions unguessable even from a leaked database.
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(UserSession.IdLength / 2)),
            UserId = UserId(ticket.Principal),
            Ticket = TicketSerializer.Default.Serialize(ticket),
            CreatedUtc = now,
            ExpiresUtc = ticket.Properties.ExpiresUtc,
        };

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        // Sign-ins are rare, so this is a good time to remove the sessions that expired.
        db.UserSessions.RemoveRange(await db.UserSessions.Where(s => s.ExpiresUtc < now).ToListAsync());
        db.UserSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var session = await db.UserSessions.FindAsync(key);
        // Ended in the meantime (signed out on another device): it stays ended.
        if (session is null)
        {
            return;
        }

        // A session belongs to one user for good. The cookie handler renews the session of the request when it signs
        // in; if that is another user, the session ends rather than changing hands (a copy of its cookie would then be
        // signed in as that user). The sign-in endpoint does not get here (StartsNewSession).
        if (ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier) != session.UserId)
        {
            db.UserSessions.Remove(session);
            await db.SaveChangesAsync();
            return;
        }

        session.Ticket = TicketSerializer.Default.Serialize(ticket);
        session.ExpiresUtc = ticket.Properties.ExpiresUtc;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The cookie handler reads the session of the request (and keeps its key) before an endpoint runs. On a sign-in
    /// endpoint (<see cref="StartsNewSession"/>), the request is not in its session: else the sign-in would renew that
    /// session and keep its key, and a copy of the earlier cookie would be signed in as whoever signs in now (session
    /// fixation). The sign-in stores a new session with a new key, and <see cref="EndPreviousSessionAsync"/> ends the
    /// earlier one.
    /// </summary>
    public Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (httpContext.GetEndpoint()?.Metadata.GetMetadata<StartsNewSession>() is not null)
        {
            httpContext.Items[PreviousSessionItem] = key;
            return Task.FromResult<AuthenticationTicket?>(null);
        }
        return RetrieveAsync(key);
    }

    /// <summary>After a sign-in on a <see cref="StartsNewSession"/> endpoint: ends the session the request came with.</summary>
    public async Task EndPreviousSessionAsync(HttpContext httpContext)
    {
        if (httpContext.Items[PreviousSessionItem] is string key)
        {
            await RemoveAsync(key);
        }
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var session = await db.UserSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == key);
        if (session is null || session.ExpiresUtc < timeProvider.GetUtcNow())
        {
            return null;
        }

        var ticket = TicketSerializer.Default.Deserialize(session.Ticket);
        var identity = ticket?.Principal.Identities.FirstOrDefault();
        if (identity is not null && !identity.HasClaim(c => c.Type == SessionKeyClaim))
        {
            identity.AddClaim(new Claim(SessionKeyClaim, key));
        }
        return ticket;
    }

    public async Task RemoveAsync(string key)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var session = await db.UserSessions.FindAsync(key);
        if (session is null)
        {
            return;
        }
        db.UserSessions.Remove(session);
        await db.SaveChangesAsync();
    }

    /// <summary>Ends every session of the user except the session of this request. Returns how many ended.</summary>
    public async Task<int> RemoveOtherSessionsAsync(ClaimsPrincipal current)
    {
        var userId = UserId(current);
        // Without its own key, it would end every session, this one too.
        var currentKey = current.FindFirstValue(SessionKeyClaim)
            ?? throw new InvalidOperationException("The key of the current session is not on the signed-in user.");

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var others = await db.UserSessions.Where(s => s.UserId == userId && s.Id != currentKey).ToListAsync();
        db.UserSessions.RemoveRange(others);
        await db.SaveChangesAsync();
        return others.Count;
    }

    private static string UserId(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("A sign-in without a user id cannot be stored.");
}

/// <summary>
/// Endpoint metadata for the sign-in endpoint: a sign-in always starts a new session (<see cref="DbTicketStore"/>).
/// </summary>
internal sealed class StartsNewSession
{
    public static readonly StartsNewSession Instance = new();

    private StartsNewSession()
    {
    }
}
