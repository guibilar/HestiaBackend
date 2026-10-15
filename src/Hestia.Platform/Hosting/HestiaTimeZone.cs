using Microsoft.Extensions.Configuration;

namespace Hestia.Platform.Hosting;

/// <summary>
/// The one time zone that decides which day it is for all modules (T85): <c>Hestia:TimeZone</c> (Docker: <c>TZ</c>). A
/// module setting <c>&lt;Module&gt;:TimeZone</c> is an optional override; the host logs a warning when one differs.
/// </summary>
public static class HestiaTimeZone
{
    public const string SettingKey = "Hestia:TimeZone";

    /// <summary>The zone when nothing is set.</summary>
    public const string Default = "UTC";

    /// <summary>The shared zone: <c>Hestia:TimeZone</c>, else <see cref="Default"/>.</summary>
    public static string Shared(IConfiguration configuration) => NonEmpty(configuration[SettingKey]) ?? Default;

    /// <summary>The zone of one module: its own <c>&lt;section&gt;:TimeZone</c> when set, else the shared zone.</summary>
    public static string For(IConfiguration configuration, string section) =>
        Override(configuration, section) ?? Shared(configuration);

    /// <summary>The module's own zone setting, or null when it uses the shared zone.</summary>
    public static string? Override(IConfiguration configuration, string section) =>
        NonEmpty(configuration[$"{section}:TimeZone"]);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
