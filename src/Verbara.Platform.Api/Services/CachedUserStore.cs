using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Redis;
using Microsoft.Extensions.Caching.Memory;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// AHH Phase 1 — IMemoryCache decorator over <see cref="IUserStore"/>. Caches
/// <see cref="GetByEmailAsync"/> and <see cref="GetByIdAsync"/> reads keyed by
/// <c>(tenantId, email)</c> / <c>(tenantId, userId)</c> respectively; every write passes
/// through and then invalidates both entries for the affected user, on this replica and — through
/// <see cref="IAuthCachePublisher"/> — on the others.
/// </summary>
/// <remarks>
/// <para>
/// Removes 5–10 ms × 1 DB round-trip per <c>POST /auth/login</c> +
/// <c>GET /auth/me</c> on the cache hit path.
/// </para>
/// <para>
/// <b>Trust boundary.</b> The cached <see cref="User"/> object includes the
/// <see cref="User.PasswordHash"/> field. This is acceptable because the
/// cache lives in-process via <see cref="IMemoryCache"/> — Postgres already
/// holds the same hash, and the process-memory boundary is the same trust
/// envelope as DataProtection keyrings + JWT signing keys. The hash is
/// <b>never</b> serialized to Redis; the
/// <c>RedisAuthCacheInvalidator</c> pubsub channel only carries invalidation
/// keys (no values). See ADR-0010 §"Trust boundary" for the canonical
/// statement.
/// </para>
/// <para>
/// <b>Copies.</b> A cache hit returns a copy of the cached user, never the cached object itself:
/// requests change the user they read (a failed sign-in, a login's lockout reset), and a change made
/// to a shared instance would reach every other request on this replica for up to the TTL without
/// ever being written.
/// </para>
/// <para>
/// Cross-replica invalidation is delivered through
/// <c>Verbara.Platform.Identity.Redis.RedisAuthCacheInvalidator</c> when
/// Redis is registered; without Redis, staleness is bounded by the local TTL.
/// </para>
/// </remarks>
internal sealed class CachedUserStore : IUserStore, ILocalAuthCacheInvalidationSink
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    private readonly IUserStore _inner;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;
    private readonly IAuthCachePublisher? _invalidator;

    public CachedUserStore(
        IUserStore inner,
        IMemoryCache cache,
        IAuthCachePublisher? invalidator = null,
        TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);

        _inner = inner;
        _cache = cache;
        _invalidator = invalidator;
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>Cache key for a user resolved by id.</summary>
    public static string ByIdKey(string tenantId, string userId) =>
        $"user:byid:{tenantId}:{userId}";

    /// <summary>Cache key for a user resolved by email (lower-cased like the DB lookup).</summary>
    public static string ByEmailKey(string tenantId, string email) =>
        $"user:byemail:{tenantId}:{email.ToLowerInvariant()}";

    public async Task<User?> GetByIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        var key = ByIdKey(tenantId.Value, userId.Value);
        if (_cache.TryGetValue<User?>(key, out var cached))
            return cached?.Clone();

        var fresh = await _inner.GetByIdAsync(tenantId, userId, ct).ConfigureAwait(false);
        _cache.Set(key, fresh, _ttl);
        // Co-populate the by-email index so the next /auth/login on the same
        // user hits cache without needing a separate DB round-trip.
        if (fresh is not null)
            _cache.Set(ByEmailKey(tenantId.Value, fresh.Email), fresh, _ttl);
        return fresh?.Clone();
    }

    public async Task<User?> GetByEmailAsync(TenantId tenantId, string email, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(email);

        var key = ByEmailKey(tenantId.Value, email);
        if (_cache.TryGetValue<User?>(key, out var cached))
            return cached?.Clone();

        var fresh = await _inner.GetByEmailAsync(tenantId, email, ct).ConfigureAwait(false);
        _cache.Set(key, fresh, _ttl);
        if (fresh is not null)
            _cache.Set(ByIdKey(tenantId.Value, fresh.UserId.Value), fresh, _ttl);
        return fresh?.Clone();
    }

    // OIDC-subject lookup is not on the password-login hot path; pass through.
    public Task<User?> FindByOidcSubjectAsync(TenantId tenantId, string oidcSubject, CancellationToken ct)
        => _inner.FindByOidcSubjectAsync(tenantId, oidcSubject, ct);

    // List + bulk-by-ids are admin/back-office paths; keep them pass-through to
    // avoid stale paginated views drifting under invalidation.
    public Task<PagedResult<User>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
        => _inner.ListAsync(tenantId, query, ct);

    // v1.14.3 — pass-through email-filter overload (R5.5 P0 finding #5 fix).
    public Task<PagedResult<User>> ListAsync(TenantId tenantId, PagedQuery query, string? email, CancellationToken ct)
        => _inner.ListAsync(tenantId, query, email, ct);

    public Task<IReadOnlyList<User>> GetByIdsAsync(string tenantId, IReadOnlyCollection<string> userIds, CancellationToken ct)
        => _inner.GetByIdsAsync(tenantId, userIds, ct);

    public async Task CreateAsync(User user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        await _inner.CreateAsync(user, ct).ConfigureAwait(false);

        // A lookup made before the user existed may have cached "no such user" under either key.
        await InvalidateAfterWriteAsync(user.TenantId, user.UserId, user.Email, ct).ConfigureAwait(false);
    }

    public Task<bool> UpdateProfileAsync(
        TenantId tenantId, EntityId userId, UserProfileChange change, DateTimeOffset updatedAt, CancellationToken ct)
        => WriteThroughAsync(tenantId, userId, () => _inner.UpdateProfileAsync(tenantId, userId, change, updatedAt, ct), ct);

    public Task<AdminFieldsWriteResult> UpdateAdminFieldsAsync(
        TenantId tenantId, EntityId userId, AdminFieldsChange change, DateTimeOffset updatedAt, string? updatedBy,
        CancellationToken ct)
        => WriteThroughAsync(tenantId, userId,
            () => _inner.UpdateAdminFieldsAsync(tenantId, userId, change, updatedAt, updatedBy, ct), ct);

    public Task<bool> SetPasswordHashAsync(
        TenantId tenantId, EntityId userId, string newHash, DateTimeOffset changedAt, bool clearLockout,
        string? expectedCurrentHash, CancellationToken ct)
        => WriteThroughAsync(tenantId, userId,
            () => _inner.SetPasswordHashAsync(tenantId, userId, newHash, changedAt, clearLockout, expectedCurrentHash, ct), ct);

    public Task<bool> RehashPasswordAsync(
        TenantId tenantId, EntityId userId, string expectedHash, string newHash, CancellationToken ct)
        => WriteThroughAsync(tenantId, userId, () => _inner.RehashPasswordAsync(tenantId, userId, expectedHash, newHash, ct), ct);

    public Task<FailedSignInResult?> RecordFailedSignInAsync(
        TenantId tenantId, EntityId userId, int threshold, DateTimeOffset lockUntil, CancellationToken ct)
        => WriteThroughAsync(tenantId, userId,
            () => _inner.RecordFailedSignInAsync(tenantId, userId, threshold, lockUntil, ct), ct);

    public Task<bool> ResetLockoutAsync(
        TenantId tenantId, EntityId userId, DateTimeOffset now, bool onlyIfUnlocked, CancellationToken ct)
        => WriteThroughAsync(tenantId, userId, () => _inner.ResetLockoutAsync(tenantId, userId, now, onlyIfUnlocked, ct), ct);

    public Task<bool> SetLastLoginAtAsync(TenantId tenantId, EntityId userId, DateTimeOffset at, CancellationToken ct)
        => WriteThroughAsync(tenantId, userId, () => _inner.SetLastLoginAtAsync(tenantId, userId, at, ct), ct);

    public Task<bool> SetPendingMfaAsync(
        TenantId tenantId, EntityId userId, string secret, IReadOnlyList<string> recoveryCodeDigests,
        DateTimeOffset updatedAt, CancellationToken ct)
        => WriteThroughAsync(tenantId, userId,
            () => _inner.SetPendingMfaAsync(tenantId, userId, secret, recoveryCodeDigests, updatedAt, ct), ct);

    public Task<bool> EnableMfaAsync(TenantId tenantId, EntityId userId, DateTimeOffset confirmedAt, CancellationToken ct)
        => WriteThroughAsync(tenantId, userId, () => _inner.EnableMfaAsync(tenantId, userId, confirmedAt, ct), ct);

    public Task<bool> ClearMfaAsync(
        TenantId tenantId, EntityId userId, bool clearLockout, DateTimeOffset updatedAt, CancellationToken ct)
        => WriteThroughAsync(tenantId, userId, () => _inner.ClearMfaAsync(tenantId, userId, clearLockout, updatedAt, ct), ct);

    public Task<bool> SetRecoveryCodesAsync(
        TenantId tenantId, EntityId userId, IReadOnlyList<string> recoveryCodeDigests, DateTimeOffset updatedAt,
        CancellationToken ct)
        => WriteThroughAsync(tenantId, userId,
            () => _inner.SetRecoveryCodesAsync(tenantId, userId, recoveryCodeDigests, updatedAt, ct), ct);

    public Task<bool> ConsumeRecoveryCodeAsync(
        TenantId tenantId, EntityId userId, string recoveryCodeDigest, CancellationToken ct)
        => WriteThroughAsync(tenantId, userId,
            () => _inner.ConsumeRecoveryCodeAsync(tenantId, userId, recoveryCodeDigest, ct), ct);

    public Task<bool> DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
        => WriteThroughAsync(tenantId, userId, () => _inner.DeleteAsync(tenantId, userId, ct), ct);

    // Every write: capture the email (the by-email key needs it and no targeted write carries it),
    // write, then drop both entries here and publish the invalidation to the other replicas — also
    // when the write was refused, since a refusal means the stored row is not what this replica may
    // have cached. A user's email never changes, so reading it before the write is enough.
    private async Task<T> WriteThroughAsync<T>(TenantId tenantId, EntityId userId, Func<Task<T>> write, CancellationToken ct)
    {
        var email = await EmailOfAsync(tenantId, userId, ct).ConfigureAwait(false);
        var result = await write().ConfigureAwait(false);
        await InvalidateAfterWriteAsync(tenantId, userId, email, ct).ConfigureAwait(false);
        return result;
    }

    // The by-email entry holds the same object as the by-id one; leaving it behind would let a
    // password sign-in read the pre-change state for up to the TTL. The email comes from the cached
    // user, or from the inner store on a miss.
    private async Task<string?> EmailOfAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        if (_cache.TryGetValue<User?>(ByIdKey(tenantId.Value, userId.Value), out var cached) && cached is not null)
            return cached.Email;
        var fresh = await _inner.GetByIdAsync(tenantId, userId, ct).ConfigureAwait(false);
        return fresh?.Email;
    }

    private async Task InvalidateAfterWriteAsync(TenantId tenantId, EntityId userId, string? email, CancellationToken ct)
    {
        InvalidateUser(tenantId.Value, userId.Value, email);
        if (_invalidator is not null)
            await _invalidator.PublishUserAsync(tenantId.Value, userId.Value, email, ct).ConfigureAwait(false);
    }

    // ─── ILocalAuthCacheInvalidationSink ────────────────────────────────────

    public void InvalidateUser(string tenantId, string userId, string? email)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(userId);

        _cache.Remove(ByIdKey(tenantId, userId));
        if (!string.IsNullOrEmpty(email))
            _cache.Remove(ByEmailKey(tenantId, email));
    }

    public void InvalidateTenantAuth(string tenantId)
    {
        // Not our concern — handled by CachedTenantAuthConfigStore.
    }

    public void InvalidatePermissions(string tenantId, string userId)
    {
        // Not our concern — handled by PermissionResolver sink.
    }
}
