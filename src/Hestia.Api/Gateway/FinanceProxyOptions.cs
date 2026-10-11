namespace Hestia.Api.Gateway;

/// <summary>
/// The finance route table (configuration section <c>FinanceProxy</c>). The host sends each request whose path
/// starts with one of <see cref="Paths"/> to the finance backend at <see cref="Address"/>, unchanged.
/// </summary>
public sealed class FinanceProxyOptions
{
    public const string SectionName = "FinanceProxy";

    /// <summary>The finance backend, without a path. Requests keep their own path and query string.</summary>
    public Uri? Address { get; set; }

    /// <summary>
    /// The finance path prefixes, for example <c>/api/Transaction</c>. Each prefix matches itself and every
    /// path below it, in any letter case. A module endpoint on the same path takes priority.
    /// </summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>The finance readiness endpoint, which <c>/health/ready</c> of the host calls.</summary>
    public string HealthPath { get; set; } = "/health/ready";

    /// <summary>How long the readiness check waits for finance.</summary>
    public TimeSpan HealthTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a proxied request can stay idle (no data in either direction) before the host stops it.</summary>
    public TimeSpan ActivityTimeout { get; set; } = TimeSpan.FromSeconds(100);

    internal IEnumerable<string> Validate()
    {
        if (Address is null || !Address.IsAbsoluteUri || (Address.Scheme != Uri.UriSchemeHttp && Address.Scheme != Uri.UriSchemeHttps))
        {
            yield return $"{SectionName}:Address must be an absolute http or https address.";
        }
        else if (Address.AbsolutePath != "/" || Address.Query.Length > 0)
        {
            yield return $"{SectionName}:Address must not have a path or a query string.";
        }
        foreach (var path in Paths)
        {
            if (!path.StartsWith('/') || path.EndsWith('/') || path.Contains('{') || path.Contains('?'))
            {
                yield return $"{SectionName}:Paths: '{path}' must start with '/', must not end with '/', and must be a plain path.";
            }
        }
        if (!HealthPath.StartsWith('/'))
        {
            yield return $"{SectionName}:HealthPath must start with '/'.";
        }
        if (HealthTimeout <= TimeSpan.Zero || ActivityTimeout <= TimeSpan.Zero)
        {
            yield return $"{SectionName}: the timeouts must be more than zero.";
        }
    }
}
