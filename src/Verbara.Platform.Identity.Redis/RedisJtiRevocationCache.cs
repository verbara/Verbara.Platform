using Verbara.Platform.Identity.Auth;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Verbara.Platform.Identity.Redis;

/// <summary>
/// Redis-backed implementation of <see cref="IJtiRevocationCache"/> per R5.2 B.9. Closes
/// R5.1 limitation #8 — the in-memory default only survives a single process, so multi-
/// instance/HA deploys would silently lose revocation state on failover or rolling restart.
/// </summary>
/// <remarks>
/// Persists each revoked jti as a marker string keyed by
/// <c>{prefix}jti:revoked:{jti}</c> with TTL bounded by the original token's expiry —
/// no orphaned keys after natural expiry. Uses <c>SETEX</c> semantics via
/// <c>StringSetAsync(value, ttl)</c>. Same key-prefix convention as
/// <see cref="RedisMfaPendingCache"/> and <see cref="RedisPasswordResetCache"/> for
/// operator visibility (single <c>asterisk:</c> namespace by default).
/// </remarks>
public sealed class RedisJtiRevocationCache : IJtiRevocationCache
{
    private const string Marker = "1";
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisIdentityOptions _options;

    internal RedisJtiRevocationCache(IConnectionMultiplexer redis, RedisIdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);
        _redis = redis;
        _options = options;
    }

    /// <summary>Create a new cache from a multiplexer and bound <see cref="IOptions{TOptions}"/>.</summary>
    public RedisJtiRevocationCache(IConnectionMultiplexer redis, IOptions<RedisIdentityOptions> options)
        : this(redis, (options ?? throw new ArgumentNullException(nameof(options))).Value)
    {
    }

    private IDatabase GetDatabase() => _redis.GetDatabase(_options.DatabaseIndex);

    private RedisKey Key(string jti) => $"{_options.KeyPrefix}jti:revoked:{jti}";

    /// <inheritdoc />
    public async ValueTask RevokeAsync(string jti, DateTimeOffset expiresAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(jti);

        var ttl = expiresAt - DateTimeOffset.UtcNow;
        if (ttl <= TimeSpan.Zero)
            return; // Already-expired token poses no threat — don't store an orphan key.

        await GetDatabase()
            .StringSetAsync(Key(jti), Marker, ttl)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsRevokedAsync(string jti, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(jti))
            return false;

        return await GetDatabase()
            .KeyExistsAsync(Key(jti))
            .ConfigureAwait(false);
    }
}
