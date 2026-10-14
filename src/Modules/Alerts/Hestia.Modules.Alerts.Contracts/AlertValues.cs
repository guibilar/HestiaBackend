namespace Hestia.Modules.Alerts.Contracts;

/// <summary>How an alert looks. The same names as the Today card severities.</summary>
public enum AlertSeverity
{
    Neutral,
    Good,
    Warning,
    Bad
}

public enum AlertValueType
{
    Money,
    Count,
    Percent,
    Date,
    Text
}

/// <summary>
/// One value in an alert text, in the same format as a Today card value: exactly one of <see cref="Number"/>,
/// <see cref="Text"/>, and <see cref="Date"/> is set, as <see cref="Type"/> says. The clients format it (D11, T08).
/// </summary>
public sealed record AlertValue(AlertValueType Type, decimal? Number = null, string? Text = null, DateOnly? Date = null)
{
    /// <summary>An amount of money. Clients format it (and hide it in privacy mode).</summary>
    public static AlertValue Money(decimal amount) => new(AlertValueType.Money, Number: amount);

    /// <summary>A whole number, for example a count of transactions or days.</summary>
    public static AlertValue Count(int count) => new(AlertValueType.Count, Number: count);

    /// <summary>A percentage, already rounded, for example 120 for "120%".</summary>
    public static AlertValue Percent(decimal percent) => new(AlertValueType.Percent, Number: percent);

    /// <summary>A day. Clients format it, for example "Jun 30, 2026".</summary>
    public static AlertValue Day(DateOnly date) => new(AlertValueType.Date, Date: date);

    /// <summary>Plain text, shown as it is, for example a name.</summary>
    public static AlertValue Plain(string text) => new(AlertValueType.Text, Text: text);
}
