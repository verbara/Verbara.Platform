using System.Globalization;
using StackExchange.Redis;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// One live Agent Assist session as the Monitor list reads it (agent-assist-works-end-to-end, design D12, D13): the tenant
/// it is filed under, the call members the list shows and the label of the session's latest caller sentiment reading.
/// The conversation and the agent's name are not stored: the list resolves them in the caller's tenant when it is read,
/// so a row can never carry a conversation or a name of a tenant it is no longer filed under.
/// </summary>
internal sealed record LiveSessionRecord(
    string SessionId,
    string OwnerTenant,
    string? AgentId,
    string? QueueName,
    string? CallerIdNum,
    DateTimeOffset? ConnectedAt,
    string? LinkedId,
    string? Sentiment = null)
{
    /// <summary>
    /// The record of session <paramref name="sessionId"/> on <paramref name="call"/>, filed under
    /// <paramref name="ownerTenant"/>. The agent id is the one the session's live events carry
    /// (<see cref="AgentAssistBridge.DeriveAgentId"/> in the owner tenant), or <see langword="null"/> when none is known.
    /// </summary>
    public static LiveSessionRecord Of(string sessionId, string ownerTenant, CallSession? call, string? sentiment = null) =>
        new(sessionId, ownerTenant, AgentIdOf(ownerTenant, call), call?.QueueName, call?.CallerIdNum, call?.ConnectedAt,
            call?.LinkedId, sentiment);

    private static string? AgentIdOf(string ownerTenant, CallSession? call)
    {
        if (call is null)
            return null;
        var agentId = AgentAssistBridge.DeriveAgentId(ownerTenant, call.AgentInterface, call.AgentId);
        return string.IsNullOrWhiteSpace(agentId) ? null : agentId;
    }
}

/// <summary>
/// Where the Monitor list of live Agent Assist sessions is kept (design D12). The engine's replica files and removes each
/// session's record (<see cref="AgentAssistSessionFiling"/>); any replica lists a tenant's records.
/// </summary>
internal interface IAgentAssistLiveSessionStore
{
    /// <summary>
    /// Whether <see cref="FileAsync"/> and <see cref="RemoveAsync"/> write anything. <see langword="false"/> for the
    /// in-process store, which reads the engine's own registry instead.
    /// </summary>
    bool IsShared { get; }

    /// <summary>Files <paramref name="record"/> under its owner tenant until <paramref name="expiresAt"/>, replacing the previous copy.</summary>
    Task FileAsync(LiveSessionRecord record, DateTimeOffset expiresAt, CancellationToken ct);

    /// <summary>Removes the record of <paramref name="sessionId"/> filed under <paramref name="ownerTenant"/>, if any.</summary>
    Task RemoveAsync(string ownerTenant, string sessionId, CancellationToken ct);

    /// <summary>The unexpired records filed under <paramref name="tenant"/>.</summary>
    Task<IReadOnlyList<LiveSessionRecord>> ListAsync(string tenant, CancellationToken ct);

    /// <summary>
    /// The unexpired record of <paramref name="sessionId"/> filed under <paramref name="callerTenant"/>, or
    /// <see langword="null"/> (supervisor-whisper-reaches-the-agent, design D2): exactly the record
    /// <see cref="ListAsync"/> returns for that id, or <see langword="null"/> when <see cref="ListAsync"/> omits it. Another
    /// tenant's session, an ownerless or conflicted one, an ended one and an unknown id are all not found.
    /// </summary>
    Task<LiveSessionRecord?> FindAsync(string callerTenant, string sessionId, CancellationToken ct);
}

/// <summary>
/// The list read on this replica only, when no Redis is configured (one Api replica): the engine's live sessions, each
/// owned by <see cref="AgentAssistLiveSessions.OwnerTenantOf"/> recomputed on every read. Writes are not needed.
/// </summary>
internal sealed class InProcessAgentAssistLiveSessionStore(IAgentAssistLiveSessions liveSessions) : IAgentAssistLiveSessionStore
{
    /// <inheritdoc />
    public bool IsShared => false;

    /// <inheritdoc />
    public Task FileAsync(LiveSessionRecord record, DateTimeOffset expiresAt, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public Task RemoveAsync(string ownerTenant, string sessionId, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<LiveSessionRecord>> ListAsync(string tenant, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LiveSessionRecord>>(
        [
            .. liveSessions.Snapshot()
                .Where(s => string.Equals(s.OwnerTenant, tenant, StringComparison.Ordinal))
                .Select(s => LiveSessionRecord.Of(s.SessionId, tenant, s.Call, s.Sentiment)),
        ]);

    /// <inheritdoc />
    public Task<LiveSessionRecord?> FindAsync(string callerTenant, string sessionId, CancellationToken ct)
    {
        foreach (var session in liveSessions.Snapshot())
        {
            if (string.Equals(session.SessionId, sessionId, StringComparison.Ordinal)
                && string.Equals(session.OwnerTenant, callerTenant, StringComparison.Ordinal))
            {
                return Task.FromResult<LiveSessionRecord?>(
                    LiveSessionRecord.Of(session.SessionId, callerTenant, session.Call, session.Sentiment));
            }
        }

        return Task.FromResult<LiveSessionRecord?>(null);
    }
}

/// <summary>
/// The list every replica reads, kept in Redis (design D12). Each tenant has a sorted set of its session ids scored by
/// expiry (Unix milliseconds) and each session a hash with a time to live. A reader drops the members whose expiry has
/// passed, then reads the hashes, so a record outlives its last heartbeat by at most <see cref="AgentAssistSessionFiling.RecordTtl"/>
/// on the index's clock, whatever Redis's own expiry does.
/// </summary>
internal sealed class RedisAgentAssistLiveSessionStore(IConnectionMultiplexer redis, TimeProvider time) : IAgentAssistLiveSessionStore
{
    internal const string KeyPrefix = "verbara:agentassist:live";

    private static readonly RedisValue AgentIdField = "agentId";
    private static readonly RedisValue QueueNameField = "queueName";
    private static readonly RedisValue CallerIdNumField = "callerIdNum";
    private static readonly RedisValue ConnectedAtField = "connectedAt";
    private static readonly RedisValue LinkedIdField = "linkedId";
    private static readonly RedisValue SentimentField = "sentiment";

    /// <inheritdoc />
    public bool IsShared => true;

    internal static RedisKey IndexKey(string tenant) => $"{KeyPrefix}:{{{tenant}}}:index";

    internal static RedisKey RecordKey(string tenant, string sessionId) => $"{KeyPrefix}:{{{tenant}}}:session:{sessionId}";

    /// <inheritdoc />
    public async Task FileAsync(LiveSessionRecord record, DateTimeOffset expiresAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var ttl = expiresAt - time.GetUtcNow();
        if (ttl <= TimeSpan.Zero)
            ttl = TimeSpan.FromMilliseconds(1);

        var key = RecordKey(record.OwnerTenant, record.SessionId);
        var index = IndexKey(record.OwnerTenant);
        var db = redis.GetDatabase();
        var tx = db.CreateTransaction();
        _ = tx.KeyDeleteAsync(key);
        _ = tx.HashSetAsync(key, [.. FieldsOf(record)]);
        _ = tx.KeyExpireAsync(key, ttl);
        _ = tx.SortedSetAddAsync(index, record.SessionId, expiresAt.ToUnixTimeMilliseconds());
        // Every record lives RecordTtl past its last filing, so the index outlives each of its members.
        _ = tx.KeyExpireAsync(index, ttl);
        await tx.ExecuteAsync().WaitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string ownerTenant, string sessionId, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var tx = db.CreateTransaction();
        _ = tx.SortedSetRemoveAsync(IndexKey(ownerTenant), sessionId);
        _ = tx.KeyDeleteAsync(RecordKey(ownerTenant, sessionId));
        await tx.ExecuteAsync().WaitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LiveSessionRecord>> ListAsync(string tenant, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var index = IndexKey(tenant);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        await db.SortedSetRemoveRangeByScoreAsync(index, double.NegativeInfinity, now).WaitAsync(ct).ConfigureAwait(false);
        var ids = await db.SortedSetRangeByScoreAsync(index, now, double.PositiveInfinity, Exclude.Start).WaitAsync(ct).ConfigureAwait(false);

        var records = new List<LiveSessionRecord>(ids.Length);
        foreach (var sessionId in ids.Select(id => id.ToString()))
        {
            var fields = await db.HashGetAllAsync(RecordKey(tenant, sessionId)).WaitAsync(ct).ConfigureAwait(false);
            if (RecordOf(tenant, sessionId, fields) is { } record)
                records.Add(record);   // an empty hash expired between the index read and this one
        }

        return records;
    }

    /// <inheritdoc />
    public async Task<LiveSessionRecord?> FindAsync(string callerTenant, string sessionId, CancellationToken ct)
    {
        // ListAsync's semantics for one member: the index score must be strictly after now, then the hash must exist.
        var db = redis.GetDatabase();
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var expiresAt = await db.SortedSetScoreAsync(IndexKey(callerTenant), sessionId).WaitAsync(ct).ConfigureAwait(false);
        if (expiresAt is not { } score || score <= now)
            return null;

        var fields = await db.HashGetAllAsync(RecordKey(callerTenant, sessionId)).WaitAsync(ct).ConfigureAwait(false);
        return RecordOf(callerTenant, sessionId, fields);
    }

    private static LiveSessionRecord? RecordOf(string tenant, string sessionId, HashEntry[] fields)
    {
        if (fields.Length == 0)
            return null;

        var values = fields.ToDictionary(f => f.Name, f => f.Value);
        return new LiveSessionRecord(
            sessionId,
            tenant,
            StringOf(values, AgentIdField),
            StringOf(values, QueueNameField),
            StringOf(values, CallerIdNumField),
            values.TryGetValue(ConnectedAtField, out var at) && long.TryParse(at.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : null,
            StringOf(values, LinkedIdField),
            StringOf(values, SentimentField));
    }

    private static IEnumerable<HashEntry> FieldsOf(LiveSessionRecord record)
    {
        // A field always exists, so a record with no call members is still a hash; an unknown member is left out.
        yield return new HashEntry("sessionId", record.SessionId);
        if (record.AgentId is { } agentId)
            yield return new HashEntry(AgentIdField, agentId);
        if (record.QueueName is { } queueName)
            yield return new HashEntry(QueueNameField, queueName);
        if (record.CallerIdNum is { } callerIdNum)
            yield return new HashEntry(CallerIdNumField, callerIdNum);
        if (record.ConnectedAt is { } connectedAt)
            yield return new HashEntry(ConnectedAtField, connectedAt.ToUnixTimeMilliseconds());
        if (record.LinkedId is { } linkedId)
            yield return new HashEntry(LinkedIdField, linkedId);
        if (record.Sentiment is { } sentiment)
            yield return new HashEntry(SentimentField, sentiment);
    }

    private static string? StringOf(Dictionary<RedisValue, RedisValue> values, RedisValue field) =>
        values.TryGetValue(field, out var value) && !value.IsNull ? value.ToString() : null;
}
