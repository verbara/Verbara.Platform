using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Redis;
using Microsoft.Extensions.Caching.Memory;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// AHH Phase 1 — IMemoryCache decorator over <see cref="IUserStore"/>. Caches
/// <see cref="GetByEmailAsync"/> and <see cref="GetByIdAsync"/> reads keyed by
/// <c>(tenantId, email)</c> / <c>(tenantId, userId)</c> respectively;
/// <see cref="SaveAsync"/> + <see cref="DeleteAsync"/> pass through and
/// invalidate cache entries for the affected user.
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
            return cached;

        var fresh = await _inner.GetByIdAsync(tenantId, userId, ct).ConfigureAwait(false);
        _cache.Set(key, fresh, _ttl);
        // Co-populate the by-email index so the next /auth/login on the same
        // user hits cache without needing a separate DB round-trip.
        if (fresh is not null)
            _cache.Set(ByEmailKey(tenantId.Value, fresh.Email), fresh, _ttl);
        return fresh;
    }

    public async Task<User?> GetByEmailAsync(TenantId tenantId, string email, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(email);

        var key = ByEmailKey(tenantId.Value, email);
        if (_cache.TryGetValue<User?>(key, out var cached))
            return cached;

        var fresh = await _inner.GetByEmailAsync(tenantId, email, ct).ConfigureAwait(false);
        _cache.Set(key, fresh, _ttl);
        if (fresh is not null)
            _cache.Set(ByIdKey(tenantId.Value, fresh.UserId.Value), fresh, _ttl);
        return fresh;
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

    public async Task SaveAsync(User user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        await _inner.SaveAsync(user, ct).ConfigureAwait(false);

        // Invalidate AFTER the write returns so the next read goes to DB and
        // the freshly-cached value reflects what was persisted (incl. any
        // server-assigned defaults / triggers).
        _cache.Remove(ByIdKey(user.TenantId.Value, user.UserId.Value));
        _cache.Remove(ByEmailKey(user.TenantId.Value, user.Email));
        if (_invalidator is not null)
            await _invalidator.PublishUserAsync(user.TenantId.Value, user.UserId.Value, user.Email, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        // Capture email before delete so we can invalidate the by-email key too.
        var existing = _cache.TryGetValue<User?>(ByIdKey(tenantId.Value, userId.Value), out var cached)
            ? cached
            : await _inner.GetByIdAsync(tenantId, userId, ct).ConfigureAwait(false);

        await _inner.DeleteAsync(tenantId, userId, ct).ConfigureAwait(false);

        _cache.Remove(ByIdKey(tenantId.Value, userId.Value));
        if (existing is not null)
            _cache.Remove(ByEmailKey(tenantId.Value, existing.Email));
        if (_invalidator is not null)
            await _invalidator.PublishUserAsync(tenantId.Value, userId.Value, existing?.Email, ct).ConfigureAwait(false);
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
