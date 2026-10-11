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

    /// <summary>
    /// The private key that signs the gateway token of each finance request (T03): ECDSA P-256, in PEM or as PKCS#8 in
    /// base64. Required. Finance gets the public key (its setting <c>Auth:Gateway:PublicKey</c>).
    /// See <see cref="GatewayTokens"/>.
    /// </summary>
    public string? SigningKey { get; set; }

    /// <summary>How long a gateway token is valid. Short: the host makes a new one for each request.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the shared finance client (<c>IFinanceClient</c>, D13) waits for an answer.</summary>
    public TimeSpan ClientTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The finance POST endpoints that only read (finance marks them <c>[ReadOnlyQuery]</c>). The read-only token can
    /// call them through the host; it cannot call other finance POST, PUT, or DELETE endpoints. Finance checks the same
    /// rule again. A path that is not in this list is refused by the host for a read-only user (a safe default).
    /// </summary>
    public List<string> ReadOnlyQueries { get; set; } = [];

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
        foreach (var path in Paths.Concat(ReadOnlyQueries))
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
        if (HealthTimeout <= TimeSpan.Zero || ActivityTimeout <= TimeSpan.Zero || ClientTimeout <= TimeSpan.Zero)
        {
            yield return $"{SectionName}: the timeouts must be more than zero.";
        }
        if (TokenLifetime < TimeSpan.FromSeconds(10) || TokenLifetime > TimeSpan.FromMinutes(10))
        {
            yield return $"{SectionName}:TokenLifetime must be from 10 seconds to 10 minutes.";
        }
        if (SigningKeyError() is { } keyError)
        {
            yield return keyError;
        }
    }

    private string? SigningKeyError()
    {
        try
        {
            using var key = GatewaySigningKey.Load(SigningKey);
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }
    }
}
