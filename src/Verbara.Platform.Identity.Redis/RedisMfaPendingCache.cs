using System.Globalization;
using System.Text.Json;
using Verbara.Platform.Identity.Mfa;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Verbara.Platform.Identity.Redis;

/// <summary>
/// Redis-backed implementation of <see cref="IMfaPendingCache"/>, shared by every Platform API replica.
/// </summary>
/// <remarks>
/// <para>
/// A challenge is a hash at <c>{prefix}mfa:challenge:{challengeToken}</c> with two fields: <c>e</c>, the
/// entry as JSON, and <c>n</c>, the attempts used. Its TTL is the entry's remaining <c>ExpiresAt</c> window.
/// Storing and reserving each run as one Lua script, so the check, the increment and the removal on the
/// last attempt are a single atomic step on the server with no retry loop under contention; consuming is a
/// single <c>DEL</c>, which reports whether this caller removed the key.
/// </para>
/// <para>
/// The key namespace changed from the one-shot <c>mfa:pending:</c> string keys on purpose: reading a key of
/// the other version's type would fail with <c>WRONGTYPE</c>. During a rolling deploy a challenge minted by
/// the other version is therefore unknown for at most its five-minute lifetime, and the user signs in again.
/// </para>
/// <para>
/// AOT: scripts go through <see cref="IDatabaseAsync.ScriptEvaluateAsync(string, RedisKey[], RedisValue[], CommandFlags)"/>
/// with explicit key and value arrays, never StackExchange.Redis's prepared-script helper, which binds named parameters by
/// reflecting over a parameter object.
/// </para>
/// </remarks>
public sealed class RedisMfaPendingCache : IMfaPendingCache
{
    // KEYS[1] = challenge key, ARGV[1] = entry JSON, ARGV[2] = TTL in milliseconds.
    // Replaces any challenge already there, so a re-store starts again from zero attempts.
    private const string StoreScript = """
        redis.call('DEL', KEYS[1])
        redis.call('HSET', KEYS[1], 'e', ARGV[1], 'n', 0)
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        return 1
        """;

    // KEYS[1] = challenge key, ARGV[1] = MaxAttempts. Returns { entry JSON, attempt } or nil.
    // The reservation that reaches the limit deletes the challenge in the same step. HINCRBY keeps the TTL.
    private const string ReserveScript = """
        local e = redis.call('HGET', KEYS[1], 'e')
        if not e then return false end
        local n = redis.call('HINCRBY', KEYS[1], 'n', 1)
        local max = tonumber(ARGV[1])
        if n >= max then redis.call('DEL', KEYS[1]) end
        if n > max then return false end
        return { e, n }
        """;

    private static readonly RedisValue MaxAttemptsValue = MfaChallenge.MaxAttempts;

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
        $"{_options.KeyPrefix}mfa:challenge:{challengeToken}";

    /// <inheritdoc />
    public async ValueTask StoreAsync(string challengeToken, MfaPendingEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(challengeToken);
        ArgumentNullException.ThrowIfNull(entry);

        var db = GetDatabase();
        var ttl = entry.ExpiresAt - DateTimeOffset.UtcNow;
        if (ttl <= TimeSpan.Zero)
        {
            // Already expired: nothing to store, but whatever the token named before is replaced too.
            await db.KeyDeleteAsync(Key(challengeToken)).ConfigureAwait(false);
            return;
        }

        var json = JsonSerializer.Serialize(entry, IdentityJsonContext.Default.MfaPendingEntry);
        var ttlMs = Math.Max(1L, (long)Math.Ceiling(ttl.TotalMilliseconds));
        await db.ScriptEvaluateAsync(
                StoreScript,
                [Key(challengeToken)],
                [json, ttlMs.ToString(CultureInfo.InvariantCulture)])
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MfaChallengeAttempt?> TryBeginAttemptAsync(string? challengeToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(challengeToken))
            return null;

        var db = GetDatabase();
        var key = Key(challengeToken);
        var result = await db.ScriptEvaluateAsync(ReserveScript, [key], [MaxAttemptsValue]).ConfigureAwait(false);
        if (result.IsNull)
            return null;

        var parts = (RedisResult[]?)result;
        if (parts is not { Length: 2 })
            return null;

        var json = (string?)parts[0];
        var attempt = (int)parts[1];
        var entry = string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize(json, IdentityJsonContext.Default.MfaPendingEntry);

        // Belt-and-braces: honour ExpiresAt even if the TTL had not yet elapsed (clock skew between API
        // nodes and the Redis server). A challenge past it is gone for every caller.
        if (entry is null || entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await db.KeyDeleteAsync(key).ConfigureAwait(false);
            return null;
        }

        return new MfaChallengeAttempt(entry, attempt, attempt >= MfaChallenge.MaxAttempts);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryConsumeAsync(string? challengeToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(challengeToken))
            return false;

        return await GetDatabase().KeyDeleteAsync(Key(challengeToken)).ConfigureAwait(false);
    }
}
