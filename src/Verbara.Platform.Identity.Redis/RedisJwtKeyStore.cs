using System.Text.Json;
using Verbara.Platform.Identity.Auth.Jwt;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Verbara.Platform.Identity.Redis;

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
    /// <remarks>
    /// AHH Phase 3.C — atomic upsert via Redis transaction with optimistic
    /// concurrency control. The previous implementation issued the entry
    /// write and the active-pointer update as two independent Redis ops, so
    /// concurrent <c>RotateAsync</c> calls on different replicas could each
    /// "win" the active flag (last-writer-wins on the pointer; both entries
    /// persisting with <c>IsActive=true</c> in their JSON blobs). This
    /// version:
    /// <list type="number">
    ///   <item>Reads the current active pointer.</item>
    ///   <item>Builds a transaction with a <c>StringEqual</c> condition on
    ///         that pointer (CAS — committed only if the pointer hasn't moved).</item>
    ///   <item>Writes the new entry, updates the pointer, and demotes the
    ///         previously-active entry's JSON (<c>IsActive=false</c>) atomically.</item>
    ///   <item>On condition failure, retries up to 5 times. After exhaustion
    ///         throws <see cref="InvalidOperationException"/>.</item>
    /// </list>
    /// </remarks>
    public async Task UpsertAsync(JwtKeyEntry entry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);

        var db = GetDatabase();
        var json = JsonSerializer.Serialize(entry, IdentityJsonContext.Default.JwtKeyEntry);
        var ttl = entry.ExpiresAt - DateTimeOffset.UtcNow;

        if (ttl <= TimeSpan.Zero)
            return; // Already-expired entry — don't store an orphan key.

        // Non-active upsert is a single-key write — the CAS dance below only
        // matters when changing the active pointer. Short-circuit for clarity.
        if (!entry.IsActive)
        {
            await db.StringSetAsync(EntryKey(entry.KeyId), json, ttl).ConfigureAwait(false);
            return;
        }

        const int MaxAttempts = 5;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var prevActiveKid = await db.StringGetAsync(ActivePointerKey).ConfigureAwait(false);

            // If a previously-active entry exists and isn't us, demote its
            // JSON blob so GetAllAsync sees a consistent IsActive flag.
            string? prevDemotedJson = null;
            TimeSpan prevDemotedTtl = default;
            if (!prevActiveKid.IsNullOrEmpty && (string)prevActiveKid! != entry.KeyId)
            {
                var prevEntryJson = await db.StringGetAsync(EntryKey(prevActiveKid!)).ConfigureAwait(false);
                if (!prevEntryJson.IsNullOrEmpty)
                {
                    var prevEntry = JsonSerializer.Deserialize(
                        (string)prevEntryJson!,
                        IdentityJsonContext.Default.JwtKeyEntry);
                    if (prevEntry is not null && prevEntry.IsActive)
                    {
                        var demoted = prevEntry with { IsActive = false };
                        prevDemotedTtl = demoted.ExpiresAt - DateTimeOffset.UtcNow;
                        if (prevDemotedTtl > TimeSpan.Zero)
                        {
                            prevDemotedJson = JsonSerializer.Serialize(
                                demoted,
                                IdentityJsonContext.Default.JwtKeyEntry);
                        }
                    }
                }
            }

            // Transaction with CAS: the active pointer must still be what we
            // observed when we started, OR not exist at all.
            var tx = db.CreateTransaction();
            tx.AddCondition(prevActiveKid.IsNullOrEmpty
                ? Condition.KeyNotExists(ActivePointerKey)
                : Condition.StringEqual(ActivePointerKey, prevActiveKid));

            _ = tx.StringSetAsync(EntryKey(entry.KeyId), json, ttl);
            _ = tx.StringSetAsync(ActivePointerKey, entry.KeyId, ttl);
            if (prevDemotedJson is not null)
                _ = tx.StringSetAsync(EntryKey((string)prevActiveKid!), prevDemotedJson, prevDemotedTtl);

            var committed = await tx.ExecuteAsync().ConfigureAwait(false);
            if (committed)
                return;

            // Backoff lightly so concurrent rotators don't tightspin against each other.
            await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"RedisJwtKeyStore.UpsertAsync exhausted {MaxAttempts} CAS retries for keyId='{entry.KeyId}'.");
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
