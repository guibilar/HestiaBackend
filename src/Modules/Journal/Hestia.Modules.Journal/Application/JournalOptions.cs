using Microsoft.Extensions.Options;

namespace Hestia.Modules.Journal.Application;

/// <summary>The settings of the Journal module (section <c>Journal</c>).</summary>
public sealed class JournalOptions
{
    public const string SectionName = "Journal";

    /// <summary>
    /// IANA time zone (for example America/Edmonton) of this module. Without a value, the shared Hestia:TimeZone (T85,
    /// Docker: TZ) applies; set it here only to override that.
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    public TimeZoneInfo GetTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>True when the settings can work. The host checks this at start.</summary>
    public bool IsValid()
    {
        try
        {
            GetTimeZone();
            return true;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    public const string ValidationMessage = "Journal: set Journal:TimeZone to an IANA time zone (for example America/Edmonton).";
}

/// <summary>Which day it is for the Journal module: the host's clock (<see cref="TimeProvider"/>) in <c>Journal:TimeZone</c>.</summary>
public sealed class JournalDays(TimeProvider time, IOptions<JournalOptions> options)
{
    public DateTimeOffset Now => time.GetUtcNow();

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, options.Value.GetTimeZone()).DateTime);
}
