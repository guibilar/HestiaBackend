using System.Security.Cryptography;

namespace Hestia.Testing;

/// <summary>
/// The gateway signing key of the test hosts: a new ECDSA P-256 key for each test run.
/// <see cref="HestiaApiFactory"/> gives it to the host (<c>FinanceProxy:SigningKey</c>). Tests check the gateway tokens
/// with <see cref="PublicKey"/>, as finance does.
/// </summary>
public static class TestGatewayKey
{
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The private key, PKCS#8 in base64, as in <c>FinanceProxy:SigningKey</c>.</summary>
    public static string SigningKey { get; } = Convert.ToBase64String(Key.ExportPkcs8PrivateKey());

    /// <summary>The public key, in PEM, as in the finance setting <c>Auth:Gateway:PublicKey</c>.</summary>
    public static string PublicKeyPem { get; } = Key.ExportSubjectPublicKeyInfoPem();

    /// <summary>A new ECDSA object with only the public key.</summary>
    public static ECDsa PublicKey()
    {
        var key = ECDsa.Create();
        key.ImportFromPem(PublicKeyPem);
        return key;
    }
}
