using Verbara.Platform.Core.Push;
using Verbara.Platform.Identity.Auth;
using Verbara.Platform.Identity.Auth.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace Verbara.Platform.Realtime.Auth;

/// <summary>
/// Builds the <see cref="TokenValidationParameters"/> Realtime uses to validate
/// JWTs minted by Verbara.Platform.Api. Keys are resolved through the same
/// <see cref="IJwtKeyStore"/> abstraction Platform.Api consumes — when the
/// Redis-backed store (<c>Verbara.Platform.Identity.Redis</c>) is wired,
/// both services share the rotation pool transparently (ADR-0012).
/// </summary>
internal static partial class JwtValidationConfigurator
{
    private const string Issuer = "verbara-platform";
    private const string Audience = "verbara-platform";

    /// <summary>
    /// The in-memory validation keys, loaded from <see cref="IJwtKeyStore"/> off the request path.
    /// Registered as a singleton; the JwtBearer <c>OnMessageReceived</c> event awaits its first load
    /// (<see cref="JwtValidationKeySnapshot.EnsureLoadedAsync(string?, CancellationToken)"/>), never the host's start.
    /// </summary>
    public static JwtValidationKeySnapshot CreateKeySnapshot(IServiceProvider services, TimeProvider? timeProvider = null)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(JwtValidationKeySnapshot).FullName!)
            ?? NullLogger.Instance;
        return new JwtValidationKeySnapshot(
            ct => LoadKeysAsync(services, ct),
            BuildKey,
            options: null,
            timeProvider,
            new JwtValidationKeySnapshotEvents
            {
                ReloadFailed = (ex, stillServing) => LogKeyReloadFailed(logger, stillServing, ex),
            });
    }

    /// <summary>
    /// Token validation parameters whose key resolver reads the registered
    /// <see cref="JwtValidationKeySnapshot"/> (one is created when none is registered). The resolver
    /// never waits on the key store.
    /// </summary>
    public static TokenValidationParameters BuildValidationParameters(IServiceProvider services)
    {
        var keys = services.GetService<JwtValidationKeySnapshot>() ?? CreateKeySnapshot(services);
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeyResolver = (_, _, kid, _) => keys.Resolve(kid),
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = "role",
            NameClaimType = "sub",
        };
    }

    /// <summary>
    /// Refuses, at connect, a token whose <c>exp</c> has passed and an impersonation token that
    /// Platform.Api has revoked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Expired: the clock-skew grace lifetime validation otherwise grants is refused here. Every hub
    /// connection is closed when its token expires (<see cref="LiveConnectionExpiry"/>); were the
    /// client's reconnect on that same token admitted, it would be closed again at once, over and over
    /// until the grace ran out. Refused, the reconnect fails like any 401 and the client backs off
    /// until it holds a current token.
    /// </para>
    /// <para>
    /// Revoked: ending, revoking or timing out an impersonation session revokes its token in the jti
    /// revocation store (<see cref="ImpersonationTokenRevocation"/>). Realtime reads the store the Api
    /// writes when both share the identity Redis — the same one, under the same key prefix, that holds
    /// the JWT key pool Realtime validates Api tokens with. Only impersonation tokens are looked up. A
    /// store failure is not caught: the connect fails rather than admitting the token. A connection
    /// already open when its token is revoked lasts until the token expires, as above.
    /// </para>
    /// </remarks>
    public static async Task RejectExpiredOrRevokedToken(TokenValidatedContext context)
    {
        var now = (context.Options.TimeProvider ?? TimeProvider.System).GetUtcNow();
        if (LiveConnectionExpiry.Of(context.Principal) <= now)
        {
            context.Fail("The access token has expired.");
            return;
        }

        if (context.Principal is { } principal
            && ImpersonationTokenRevocation.IsImpersonation(principal)
            && await ImpersonationTokenRevocation.IsRevokedAsync(
                context.HttpContext.RequestServices.GetRequiredService<IJtiRevocationCache>(),
                principal,
                context.HttpContext.RequestAborted).ConfigureAwait(false))
        {
            context.Fail(ImpersonationTokenRevocation.RevokedMessage);
        }
    }

    private static async Task<IReadOnlyList<JwtKeyEntry>> LoadKeysAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IJwtKeyStore>();
        return await store.GetAllAsync(ct).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 6105, Level = LogLevel.Warning,
        Message = "JWT validation-key reload failed; still serving the previous keys: {StillServing}. Investigate key-pool availability.")]
    private static partial void LogKeyReloadFailed(ILogger logger, bool stillServing, Exception exception);

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
