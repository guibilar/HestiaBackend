using System.Security.Cryptography;
using Hestia.Platform.Modules;
using Microsoft.Extensions.Options;

namespace Hestia.Api.Gateway;

/// <summary>
/// The gateway signing key (T43). <c>gateway-key</c> shows the key id and the public key of the key in use.
/// <c>gateway-key new</c> makes a new key pair for a key change, and prints the lines for the two <c>.env</c> files.
/// It changes nothing: the user sets the values (README, "Change the gateway key").
/// In Docker: <c>docker compose run --rm -T hestia-api gateway-key new</c>.
/// </summary>
public sealed class GatewayKeyCommand : IModuleCommand
{
    public const string New = "new";

    public string Name => "gateway-key";

    public string Description => "Shows the gateway key in use, or makes a new key pair: gateway-key [new]";

    public async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> arguments, TextReader input,
        TextWriter output, CancellationToken cancellationToken)
    {
        var current = services.GetRequiredService<IOptions<FinanceProxyOptions>>().Value.SigningKey;
        using var currentKey = GatewaySigningKey.Load(current);
        switch (arguments)
        {
            case []:
                await output.WriteLineAsync($"Key id: {GatewaySigningKey.KeyId(currentKey)}");
                await output.WriteLineAsync($"Finance .env: GATEWAY_PUBLIC_KEY={PublicKey(currentKey)}");
                return 0;
            case [New]:
                using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    await output.WriteLineAsync($"Key in use: {GatewaySigningKey.KeyId(currentKey)}");
                    await output.WriteLineAsync($"New key:    {GatewaySigningKey.KeyId(key)}");
                    await output.WriteLineAsync();
                    await output.WriteLineAsync("1. Finance .env (finance then accepts both keys), and restart finance:");
                    await output.WriteLineAsync($"GATEWAY_NEXT_PUBLIC_KEY={PublicKey(key)}");
                    await output.WriteLineAsync("2. Hestia .env, and restart hestia-api:");
                    await output.WriteLineAsync($"GATEWAY_SIGNING_KEY={Convert.ToBase64String(key.ExportPkcs8PrivateKey())}");
                    await output.WriteLineAsync("3. Finance .env: move the value of GATEWAY_NEXT_PUBLIC_KEY to GATEWAY_PUBLIC_KEY,");
                    await output.WriteLineAsync("   make GATEWAY_NEXT_PUBLIC_KEY empty, and restart finance.");
                }
                return 0;
            default:
                await output.WriteLineAsync($"Usage: {Name} [{New}]");
                return 1;
        }
    }

    private static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
}
