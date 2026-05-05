namespace Verbara.Platform.Identity.Auth;

/// <summary>
/// Cache of revoked JWT IDs (jti claims). Entries are bounded by the original token's
/// expiry, so revoked tokens are pruned automatically once they would have expired
/// naturally.
/// </summary>
/// <remarks>
/// Promoted from internal in <c>Verbara.Platform.Api</c> to a public abstraction in
/// <c>Verbara.Platform.Identity</c> so that out-of-process implementations
/// (e.g. <c>Verbara.Platform.Identity.Redis</c>) can satisfy the contract without
/// needing <c>InternalsVisibleTo</c>. Mirrors the visibility model used by
/// <c>IMfaPendingCache</c> and <c>IPasswordResetCache</c>.
/// </remarks>
public interface IJtiRevocationCache
{
    /// <summary>Returns <see langword="true"/> when <paramref name="jti"/> has been revoked and the token would still be live.</summary>
    ValueTask<bool> IsRevokedAsync(string jti, CancellationToken ct);

    /// <summary>
    /// Marks <paramref name="jti"/> revoked until <paramref name="expiresAt"/>. Implementations
    /// must no-op when the expiry has already elapsed.
    /// </summary>
    ValueTask RevokeAsync(string jti, DateTimeOffset expiresAt, CancellationToken ct);
}
