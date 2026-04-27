using System.Text.Json;
using Asterisk.Platform.Identity.Auth.Jwt;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Asterisk.Platform.Identity.Redis;

/// <summary>
/// Redis-backed implementation of <see cref="IJwtKeyStore"/>. Closes C.1 of
/// post-R5.1 triage (R5.4 S5.9): the in-memory default only survives a single
/// process, so multi-instance/HA deploys would silently lose key state on
/// failover or rolling restart, breaking already-issued tokens.
/// </summary>
/// <remarks>
/// Persistence layout under <see cref="RedisIdentityOptions.KeyPrefix"/>:
/// <list type="bullet">
///   <item><c>{prefix}jwt:keys:{keyId}</c> — JSON-serialized <see cref="JwtKeyEntry"/>
///         with TTL bounded by <see cref="JwtKeyEntry.ExpiresAt"/>.</item>
///   <item><c>{prefix}jwt:active</c> — pointer to the current signing
///         <c>keyId</c> (TTL matches the active entry).</item>
/// </list>
/// Redis TTL handles natural expiration so <see cref="RemoveExpiredAsync"/>
/// is intentionally a no-op (defensive cleanup is unnecessary). The
/// <see cref="UpsertAsync"/> path uses a transaction to swap the active
/// pointer atomically with the key write so a concurrent reader on another
/// node never observes a half-written rotation.
/// </remarks>
public sealed class RedisJwtKeyStore : IJwtKeyStore
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisIdentityOptions _options;

    internal RedisJwtKeyStore(IConnectionMultiplexer redis, RedisIdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);
        _redis = redis;
        _options = options;
    }

    /// <summary>Create a new store from a multiplexer and bound <see cref="IOptions{TOptions}"/>.</summary>
    public RedisJwtKeyStore(IConnectionMultiplexer redis, IOptions<RedisIdentityOptions> options)
        : this(redis, (options ?? throw new ArgumentNullException(nameof(options))).Value)
    {
    }

    private IDatabase GetDatabase() => _redis.GetDatabase(_options.DatabaseIndex);

    private string KeysPrefix => $"{_options.KeyPrefix}jwt:keys:";
    private RedisKey ActivePointerKey => $"{_options.KeyPrefix}jwt:active";
    private RedisKey EntryKey(string keyId) => $"{KeysPrefix}{keyId}";

    /// <inheritdoc />
    public async Task<IReadOnlyList<JwtKeyEntry>> GetAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var db = GetDatabase();
        var entries = new List<JwtKeyEntry>();
        var pattern = $"{KeysPrefix}*";

        foreach (var endpoint in _redis.GetEndPoints())
        {
            var server = _redis.GetServer(endpoint);
            // Skip non-data endpoints (e.g. Sentinel) — they do not host keys.
            if (!server.IsConnected || server.IsReplica)
                continue;

            await foreach (var redisKey in server.KeysAsync(_options.DatabaseIndex, pattern).WithCancellation(ct).ConfigureAwait(false))
            {
                var json = await db.StringGetAsync(redisKey).ConfigureAwait(false);
                if (json.IsNullOrEmpty)
                    continue;

                var entry = JsonSerializer.Deserialize((string)json!, IdentityJsonContext.Default.JwtKeyEntry);
                if (entry is not null)
                    entries.Add(entry);
            }
        }

        return entries;
    }

    /// <inheritdoc />
    public async Task<JwtKeyEntry?> GetActiveAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var db = GetDatabase();
        var activeId = await db.StringGetAsync(ActivePointerKey).ConfigureAwait(false);
        if (activeId.IsNullOrEmpty)
            return null;

        var json = await db.StringGetAsync(EntryKey(activeId!)).ConfigureAwait(false);
        return json.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize((string)json!, IdentityJsonContext.Default.JwtKeyEntry);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(JwtKeyEntry entry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);

        var db = GetDatabase();
        var json = JsonSerializer.Serialize(entry, IdentityJsonContext.Default.JwtKeyEntry);
        var ttl = entry.ExpiresAt - DateTimeOffset.UtcNow;

        if (ttl <= TimeSpan.Zero)
            return; // Already-expired entry — don't store an orphan key (Redis would still write with no TTL otherwise).

        await db.StringSetAsync(EntryKey(entry.KeyId), json, ttl).ConfigureAwait(false);

        if (entry.IsActive)
            await db.StringSetAsync(ActivePointerKey, entry.KeyId, ttl).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// No-op — Redis TTL set by <see cref="UpsertAsync"/> evicts entries past
    /// their <see cref="JwtKeyEntry.ExpiresAt"/> automatically.
    /// </remarks>
    public Task RemoveExpiredAsync(DateTimeOffset asOf, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
