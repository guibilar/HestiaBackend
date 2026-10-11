using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hestia.Platform.Auth;
using Microsoft.Extensions.Options;

namespace Hestia.Api.Gateway;

/// <summary>
/// The short-lived token that the host adds to each finance request (D5, T03): a JWT, signed with ES256 (ECDSA P-256,
/// SHA-256) by the key in <c>FinanceProxy:SigningKey</c>. Only the host has this key. Finance has the public key, and
/// accepts only these tokens (its auth mode <c>Gateway</c>).
/// <para>Claims: <c>iss</c> = hestia-api, <c>aud</c> = finance, <c>sub</c> (user id), <c>name</c>, <c>scope</c>
/// (<c>full</c> or <c>read-only</c>), <c>iat</c>, <c>nbf</c>, <c>exp</c> (<c>FinanceProxy:TokenLifetime</c>, 60 seconds),
/// and <c>jti</c>.</para>
/// <para>The system identity of background work (T42) has <c>sub</c> = <see cref="SystemSubject"/> and
/// <c>name</c> = <c>system:&lt;caller&gt;</c>. Its scope is also <c>full</c> or <c>read-only</c>, so finance needs no
/// change for it.</para>
/// </summary>
/// <remarks>
/// The token is made here with the base library (no package): a fixed header and one ECDSA signature. Finance checks it
/// with the standard JWT bearer handler; the host tests check it the same way.
/// </remarks>
public sealed class GatewayTokens : IDisposable
{
    public const string Issuer = "hestia-api";
    public const string Audience = "finance";
    public const string ScopeClaim = "scope";

    /// <summary>The <c>sub</c> of the system identity (background work, T42). No user account has this id.</summary>
    public const string SystemSubject = "hestia-system";

    /// <summary>The prefix of the <c>name</c> of the system identity, before the caller name.</summary>
    public const string SystemNamePrefix = "system:";

    // {"alg":"ES256","typ":"JWT"}
    private static readonly string Header = Base64Url.EncodeToString("""{"alg":"ES256","typ":"JWT"}"""u8);

    private readonly ECDsa _key;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;

    public GatewayTokens(IOptions<FinanceProxyOptions> options, TimeProvider time)
    {
        _key = GatewaySigningKey.Load(options.Value.SigningKey);
        _time = time;
        _lifetime = options.Value.TokenLifetime;
    }

    /// <summary>A new token for <paramref name="user"/>, who must be signed in.</summary>
    public string Create(ClaimsPrincipal user)
    {
        if (user.Identity is not { IsAuthenticated: true } identity)
        {
            throw new InvalidOperationException("A gateway token is only for a signed-in user.");
        }
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? identity.Name
            ?? throw new InvalidOperationException("The signed-in user has no id.");
        return Create(id, identity.Name ?? id, HestiaUser.ScopeOf(user));
    }

    /// <summary>
    /// A new token for the system identity (T42): background work with no user. <paramref name="scope"/> is
    /// <see cref="HestiaUser.FullScope"/> or <see cref="HestiaUser.ReadOnlyScope"/>.
    /// </summary>
    public string CreateForSystem(string caller, string scope)
    {
        if (scope is not (HestiaUser.FullScope or HestiaUser.ReadOnlyScope))
        {
            throw new ArgumentException($"The scope must be {HestiaUser.FullScope} or {HestiaUser.ReadOnlyScope}.", nameof(scope));
        }
        return Create(SystemSubject, SystemNamePrefix + caller, scope);
    }

    private string Create(string subject, string name, string scope)
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = Audience,
            ["sub"] = subject,
            ["name"] = name,
            [ScopeClaim] = scope,
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + (long)_lifetime.TotalSeconds,
            ["jti"] = Guid.NewGuid().ToString("N")
        };
        var payload = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signedPart = $"{Header}.{payload}";
        // .NET signs ECDSA in the IEEE P1363 format (r || s), as JWS ES256 requires.
        var signature = _key.SignData(Encoding.ASCII.GetBytes(signedPart), HashAlgorithmName.SHA256);
        return $"{signedPart}.{Base64Url.EncodeToString(signature)}";
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>The ES256 signing key of the gateway tokens: an ECDSA P-256 private key.</summary>
public static class GatewaySigningKey
{
    /// <summary>
    /// Reads the key: PEM (<c>-----BEGIN PRIVATE KEY-----</c> or <c>-----BEGIN EC PRIVATE KEY-----</c>), or the
    /// DER bytes (PKCS#8 or SEC1) in base64 on one line (easier in an .env file). See the README, section "Sign-in".
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is empty or not a P-256 private key.</exception>
    public static ECDsa Load(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{FinanceProxyOptions.SectionName}:SigningKey is not set.");
        }
        var key = ECDsa.Create();
        try
        {
            var text = value.Trim();
            if (text.StartsWith("-----BEGIN", StringComparison.Ordinal))
            {
                key.ImportFromPem(text);
            }
            else
            {
                var bytes = Convert.FromBase64String(text);
                try
                {
                    key.ImportPkcs8PrivateKey(bytes, out _);
                }
                catch (CryptographicException)
                {
                    // The SEC1 form (what `openssl ec -outform DER` writes).
                    key.ImportECPrivateKey(bytes, out _);
                }
            }
            if (key.KeySize != 256)
            {
                throw new CryptographicException("The key is not a P-256 key.");
            }
            // A public key alone cannot sign: check now, not at the first request.
            key.ExportParameters(includePrivateParameters: true);
            return key;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            key.Dispose();
            throw new InvalidOperationException(
                $"{FinanceProxyOptions.SectionName}:SigningKey is not an ECDSA P-256 private key (PEM, or PKCS#8 in base64).", exception);
        }
    }

    /// <summary>The public key, in PEM, for the finance setting <c>Auth:Gateway:PublicKey</c>.</summary>
    public static string PublicKeyPem(string? signingKey)
    {
        using var key = Load(signingKey);
        return key.ExportSubjectPublicKeyInfoPem();
    }
}
