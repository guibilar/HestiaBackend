using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Hestia.Testing;

/// <summary>The claims of a valid gateway token, as finance reads them.</summary>
public sealed record GatewayToken(string Subject, string Name, string Scope, TimeSpan Lifetime, DateTimeOffset Expires)
{
    /// <summary>
    /// The finance check (T03, finance auth mode "Gateway"): the standard JWT handler, ES256 only, the test public key,
    /// issuer hestia-api, audience finance, and a valid lifetime.
    /// </summary>
    public static TokenValidationParameters FinanceValidation() => new()
    {
        ValidIssuer = "hestia-api",
        ValidAudience = "finance",
        IssuerSigningKey = new ECDsaSecurityKey(TestGatewayKey.PublicKey()),
        ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ClockSkew = TimeSpan.FromSeconds(10)
    };

    /// <summary>Checks the token as finance does. Fails the test when it is not valid.</summary>
    public static GatewayToken Validate(string token)
    {
        var result = new JsonWebTokenHandler().ValidateTokenAsync(token, FinanceValidation()).GetAwaiter().GetResult();
        if (!result.IsValid)
        {
            throw new InvalidOperationException($"Finance would refuse the gateway token: {result.Exception?.Message}");
        }
        var jwt = (JsonWebToken)result.SecurityToken;
        return new GatewayToken(
            jwt.Subject,
            jwt.GetClaim("name").Value,
            jwt.GetClaim("scope").Value,
            jwt.ValidTo - jwt.ValidFrom,
            new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero));
    }

    /// <summary>The gateway token of a request that reached the fake finance backend, checked as finance does.</summary>
    public static GatewayToken Read(FinanceRequest request)
    {
        var header = request.Header("Authorization")
            ?? throw new InvalidOperationException($"{request.Method} {request.Target} reached finance without a token.");
        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The Authorization header is not a bearer token: {header}");
        }
        return Validate(header[bearer.Length..]);
    }
}
