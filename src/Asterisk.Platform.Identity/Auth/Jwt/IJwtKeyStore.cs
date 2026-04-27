namespace Asterisk.Platform.Identity.Auth.Jwt;

/// <summary>
/// Persistence contract for the JWT signing-key rotation pool. Implementations
/// should support:
/// <list type="bullet">
///   <item>Multiple non-active keys (validation-only) coexisting with at most
///         one active (signing) key.</item>
///   <item>Survival of process restart so rolling restarts never invalidate
///         in-flight tokens.</item>
/// </list>
/// </summary>
/// <remarks>
/// R5.4 S5.9 — Closes C.1 of post-R5.1 triage. Default in-memory implementation
/// lives in <see cref="InMemoryJwtKeyStore"/>; the Redis-backed implementation
/// (used in clustered deploys for zero-downtime rotation) is provided by
/// <c>Asterisk.Platform.Identity.Redis.RedisJwtKeyStore</c>.
/// </remarks>
public interface IJwtKeyStore
{
    /// <summary>Returns every key (active + grace-window). Caller filters expired entries.</summary>
    Task<IReadOnlyList<JwtKeyEntry>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Returns the single active signing key, or <see langword="null"/> on bootstrap.</summary>
    Task<JwtKeyEntry?> GetActiveAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists <paramref name="entry"/>. If <see cref="JwtKeyEntry.IsActive"/>
    /// is <see langword="true"/>, implementations must demote any other entry
    /// currently marked active to <see langword="false"/> atomically per their
    /// guarantees.
    /// </summary>
    Task UpsertAsync(JwtKeyEntry entry, CancellationToken ct = default);

    /// <summary>Removes entries whose <see cref="JwtKeyEntry.ExpiresAt"/> is older than <paramref name="asOf"/>.</summary>
    Task RemoveExpiredAsync(DateTimeOffset asOf, CancellationToken ct = default);
}
