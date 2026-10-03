using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Verbara.Platform.Core.Push;

namespace Verbara.Platform.Identity.Auth;

/// <summary>
/// How an impersonation token stops authenticating before it expires, shared by every host that
/// accepts one (Platform.Api and Platform.Realtime). Closing an impersonation session — the
/// impersonator ends it, an admin revokes it, the timeout sweep expires it — writes the token's
/// <c>jti</c> to the <see cref="IJtiRevocationCache"/>, and each host refuses an impersonation token
/// whose <c>jti</c> is there.
/// </summary>
/// <remarks>
/// <para>
/// Only impersonation tokens are checked: one acts as Admin in another tenant for 30 minutes, which
/// is worth a lookup on every request. An ordinary access token is not looked up (its 15-minute
/// lifetime is the accepted residual) and nothing revokes one this way.
/// </para>
/// <para>
/// A revocation reaches another process only through a shared cache: with more than one replica, or
/// with Platform.Realtime, that is the Redis-backed one (<c>ConnectionStrings:IdentityRedis</c>, with
/// the same <c>Identity:Redis:KeyPrefix</c> on every host). The in-memory default holds only the
/// revocations its own process wrote.
/// </para>
/// </remarks>
public static class ImpersonationTokenRevocation
{
    /// <summary>The reason a revoked impersonation token is refused with.</summary>
    public const string RevokedMessage = "The impersonation session has ended.";

    /// <summary>
    /// The longest clock skew any host grants when it validates a Platform token's lifetime: a token
    /// is still accepted up to this long after its <c>exp</c>. A revocation is kept that much longer
    /// than the token's expiry, so the token cannot validate again inside the skew. No validator's
    /// <c>ClockSkew</c> may exceed it.
    /// </summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>Whether <paramref name="principal"/> was authenticated by an impersonation token.</summary>
    public static bool IsImpersonation(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return string.Equals(principal.FindFirst("impersonation")?.Value, "true", StringComparison.Ordinal);
    }

    /// <summary>
    /// Revokes impersonation token <paramref name="tokenId"/>, which expires at
    /// <paramref name="expiresAt"/>, until no host can validate it any more.
    /// </summary>
    /// <remarks>
    /// Idempotent. A cache failure is thrown: the caller must then leave the session open, never
    /// report it closed while its token still works.
    /// </remarks>
    public static ValueTask RevokeAsync(
        IJtiRevocationCache cache, string tokenId, DateTimeOffset expiresAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentException.ThrowIfNullOrEmpty(tokenId);
        return cache.RevokeAsync(tokenId, expiresAt + MaxClockSkew, ct);
    }

    /// <summary>
    /// Revokes the impersonation token that authenticated <paramref name="principal"/>, by its own
    /// <c>jti</c> and <c>exp</c>. That needs no record of the token's session, so it works on any
    /// replica.
    /// </summary>
    /// <remarks>
    /// A token that names no <c>jti</c> is left alone: <see cref="IsRevokedAsync"/> already refuses
    /// it. A cache failure is thrown, as for <see cref="RevokeAsync(IJtiRevocationCache, string, DateTimeOffset, CancellationToken)"/>.
    /// </remarks>
    public static async ValueTask RevokeAsync(IJtiRevocationCache cache, ClaimsPrincipal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var tokenId = TokenIdOf(principal);
        if (string.IsNullOrEmpty(tokenId))
            return;

        await RevokeAsync(cache, tokenId, LiveConnectionExpiry.Of(principal), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the impersonation token that authenticated <paramref name="principal"/> has been
    /// revoked. A token that names no <c>jti</c> counts as revoked: every impersonation token
    /// Platform.Api mints carries one.
    /// </summary>
    /// <remarks>
    /// A cache failure is thrown rather than read as "not revoked", so the request fails closed (as a
    /// server error) instead of admitting a token that may have been revoked.
    /// </remarks>
    public static async ValueTask<bool> IsRevokedAsync(IJtiRevocationCache cache, ClaimsPrincipal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(principal);
        var tokenId = TokenIdOf(principal);
        return string.IsNullOrEmpty(tokenId) || await cache.IsRevokedAsync(tokenId, ct).ConfigureAwait(false);
    }

    private static string? TokenIdOf(ClaimsPrincipal principal) =>
        principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
}
