namespace Hestia.Modules.Identity.Application;

/// <summary>
/// The sign-in settings (section <c>Identity</c>). There is no public sign-up: the account comes from here, or from the
/// finance backend with the command <c>import-finance-user</c>.
/// </summary>
public sealed class IdentityModuleOptions
{
    public const string SectionName = "Identity";

    /// <summary>The minimum length of <see cref="ReadOnlyToken"/>.</summary>
    public const int MinimumTokenLength = 32;

    public InitialUserOptions InitialUser { get; set; } = new();

    /// <summary>
    /// Opens the host read-only to <c>Authorization: Bearer &lt;token&gt;</c> (see <see cref="ReadOnlyTokenHandler"/>):
    /// for scripts and assistants. At least <see cref="MinimumTokenLength"/> characters. Empty turns it off.
    /// </summary>
    public string? ReadOnlyToken { get; set; }

    public bool IsValid() => string.IsNullOrEmpty(ReadOnlyToken) || ReadOnlyToken.Trim().Length >= MinimumTokenLength;
}

public sealed class InitialUserOptions
{
    /// <summary>The host makes this account when it starts, if no account has this name.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>Also sets the password (and ends a lockout) when the account exists. Remove it after use.</summary>
    public bool ResetPassword { get; set; }
}
