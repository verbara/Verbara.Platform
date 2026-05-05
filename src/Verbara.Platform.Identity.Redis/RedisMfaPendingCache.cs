using System.Text.Json;
using Verbara.Platform.Identity.Mfa;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Verbara.Platform.Identity.Redis;

/// <summary>
/// Redis-backed implementation of <see cref="IMfaPendingCache"/>. Persists entries as
/// JSON strings keyed by <c>{prefix}mfa:pending:{challengeToken}</c> with TTL equal to
/// the entry's remaining <c>ExpiresAt</c> window. <c>TakeAsync</c> atomically reads +
/// deletes via <c>StringGetDeleteAsync</c> so a token can only be consumed once across
/// all Platform API instances.
/// </summary>
public sealed class RedisMfaPendingCache : IMfaPendingCache
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisIdentityOptions _options;

    internal RedisMfaPendingCache(IConnectionMultiplexer redis, RedisIdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);
        _redis = redis;
        _options = options;
    }

    /// <summary>Create a new cache from a multiplexer and bound <see cref="IOptions{TOptions}"/>.</summary>
    public RedisMfaPendingCache(IConnectionMultiplexer redis, IOptions<RedisIdentityOptions> options)
        : this(redis, (options ?? throw new ArgumentNullException(nameof(options))).Value)
    {
    }

    private IDatabase GetDatabase() => _redis.GetDatabase(_options.DatabaseIndex);

    private RedisKey Key(string challengeToken) =>
        $"{_options.KeyPrefix}mfa:pending:{challengeToken}";

    /// <inheritdoc />
    public async ValueTask StoreAsync(string challengeToken, MfaPendingEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(challengeToken);
        ArgumentNullException.ThrowIfNull(entry);

        var ttl = entry.ExpiresAt - DateTimeOffset.UtcNow;
        if (ttl <= TimeSpan.Zero)
            return;

        var json = JsonSerializer.Serialize(entry, IdentityJsonContext.Default.MfaPendingEntry);
        await GetDatabase()
            .StringSetAsync(Key(challengeToken), json, ttl)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MfaPendingEntry?> TakeAsync(string challengeToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(challengeToken);

        var value = await GetDatabase()
            .StringGetDeleteAsync(Key(challengeToken))
            .ConfigureAwait(false);

        if (value.IsNullOrEmpty)
            return null;

        var entry = JsonSerializer.Deserialize(value.ToString(), IdentityJsonContext.Default.MfaPendingEntry);
        if (entry is null)
            return null;

        // Belt-and-braces: honour ExpiresAt even if TTL had not yet elapsed (clock skew).
        return entry.ExpiresAt > DateTimeOffset.UtcNow ? entry : null;
    }
}
