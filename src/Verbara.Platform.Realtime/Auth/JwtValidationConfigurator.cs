using Verbara.Platform.Identity.Auth.Jwt;
using Microsoft.IdentityModel.Tokens;

namespace Verbara.Platform.Realtime.Auth;

/// <summary>
/// Builds the <see cref="TokenValidationParameters"/> Realtime uses to validate
/// JWTs minted by Verbara.Platform.Api. Keys are resolved through the same
/// <see cref="IJwtKeyStore"/> abstraction Platform.Api consumes — when the
/// Redis-backed store (<c>Verbara.Platform.Identity.Redis</c>) is wired,
/// both services share the rotation pool transparently (ADR-0012).
/// </summary>
internal static class JwtValidationConfigurator
{
    private const string Issuer = "verbara-platform";
    private const string Audience = "verbara-platform";

    public static TokenValidationParameters BuildValidationParameters(IServiceProvider services)
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeyResolver = (_, _, _, _) => ResolveKeys(services),
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = "role",
            NameClaimType = "sub",
        };
    }

    private static List<SecurityKey> ResolveKeys(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IJwtKeyStore>();
        var entries = store.GetAllAsync().GetAwaiter().GetResult();
        var now = DateTimeOffset.UtcNow;
        var keys = new List<SecurityKey>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.ExpiresAt <= now)
                continue;
            keys.Add(BuildKey(entry));
        }
        return keys;
    }

    private static SecurityKey BuildKey(JwtKeyEntry entry) => entry.Algorithm switch
    {
        JwtKeyAlgorithm.Hs256 => new SymmetricSecurityKey(Convert.FromBase64String(entry.Key))
        {
            KeyId = entry.KeyId,
        },
        JwtKeyAlgorithm.Rs256 => BuildRsaKey(entry),
        _ => throw new NotSupportedException($"Algorithm {entry.Algorithm} is not supported by Realtime."),
    };

    private static RsaSecurityKey BuildRsaKey(JwtKeyEntry entry)
    {
        var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(entry.Key), out _);
        return new RsaSecurityKey(rsa) { KeyId = entry.KeyId };
    }

    public static bool IsQueryTokenPathAllowed(PathString path)
    {
        if (!path.HasValue)
            return false;
        return path.Value!.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ExtractJwtQueryParam(HttpRequest request)
    {
        var token = request.Query["access_token"].FirstOrDefault()
            ?? request.Query["token"].FirstOrDefault();
        return token is not null && token.StartsWith("eyJ", StringComparison.Ordinal)
            ? token
            : null;
    }
}
