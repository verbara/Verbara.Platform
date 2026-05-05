using System.Text.Json;
using Verbara.Platform.Identity.Mfa;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Verbara.Platform.Identity.Redis;

/// <summary>
/// Redis-backed implementation of <see cref="IPasswordResetCache"/>. Persists entries as
/// JSON strings keyed by <c>{prefix}mfa:passwordreset:{resetToken}</c> with TTL equal to
/// the entry's remaining <c>ExpiresAt</c> window. <c>TakeAsync</c> atomically reads +
/// deletes via <c>StringGetDeleteAsync</c> so a token can only be consumed once across
/// all Platform API instances.
/// </summary>
public sealed class RedisPasswordResetCache : IPasswordResetCache
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisIdentityOptions _options;

    internal RedisPasswordResetCache(IConnectionMultiplexer redis, RedisIdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);
        _redis = redis;
        _options = options;
    }

    /// <summary>Create a new cache from a multiplexer and bound <see cref="IOptions{TOptions}"/>.</summary>
    public RedisPasswordResetCache(IConnectionMultiplexer redis, IOptions<RedisIdentityOptions> options)
        : this(redis, (options ?? throw new ArgumentNullException(nameof(options))).Value)
    {
    }

    private IDatabase GetDatabase() => _redis.GetDatabase(_options.DatabaseIndex);

    private RedisKey Key(string resetToken) =>
        $"{_options.KeyPrefix}mfa:passwordreset:{resetToken}";

    /// <inheritdoc />
    public async ValueTask StoreAsync(string resetToken, PasswordResetEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(resetToken);
        ArgumentNullException.ThrowIfNull(entry);

        var ttl = entry.ExpiresAt - DateTimeOffset.UtcNow;
        if (ttl <= TimeSpan.Zero)
            return;

        var json = JsonSerializer.Serialize(entry, IdentityJsonContext.Default.PasswordResetEntry);
        await GetDatabase()
            .StringSetAsync(Key(resetToken), json, ttl)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<PasswordResetEntry?> TakeAsync(string resetToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(resetToken);

        var value = await GetDatabase()
            .StringGetDeleteAsync(Key(resetToken))
            .ConfigureAwait(false);

        if (value.IsNullOrEmpty)
            return null;

        var entry = JsonSerializer.Deserialize(value.ToString(), IdentityJsonContext.Default.PasswordResetEntry);
        if (entry is null)
            return null;

        return entry.ExpiresAt > DateTimeOffset.UtcNow ? entry : null;
    }
}
