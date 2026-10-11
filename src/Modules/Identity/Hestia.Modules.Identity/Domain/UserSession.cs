namespace Hestia.Modules.Identity.Domain;

/// <summary>
/// One sign-in (a browser or a device). The session cookie carries only <see cref="Id"/>. Sign-out, a password change,
/// or "sign out other devices" deletes the row, so a copy of the cookie stops working too.
/// </summary>
public class UserSession
{
    public const int IdLength = 64;

    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    /// <summary>The serialized authentication ticket (the user's claims and the cookie's properties).</summary>
    public byte[] Ticket { get; set; } = [];

    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
}
