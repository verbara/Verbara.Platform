using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Diagnostic payload stored under a W3 agent-liveness presence key. Presence is
/// determined solely by key existence + TTL; this value records which node last
/// touched the key and when, for operational debugging only.
/// </summary>
public sealed record AgentLivenessEntry(string NodeId, DateTimeOffset TouchedAt);

[JsonSerializable(typeof(AgentLivenessEntry))]
internal sealed partial class AgentLivenessJsonContext : JsonSerializerContext;

/// <summary>
/// Redis-backed <see cref="IAgentLivenessStore"/>. A routable agent's browser
/// refreshes a presence key (<c>{prefix}presence:agent:{tenantId}:{agentId}</c>)
/// with a short TTL; the liveness reaper forces Offline any routable agent whose
/// key has expired. Presence == key existence; the stored JSON is diagnostic.
/// Shares the same <see cref="IConnectionMultiplexer"/> as the rest of Identity
/// Redis, so it lives in the Api composition root rather than adding a Queues
/// reference to Identity.Redis.
/// </summary>
public sealed class RedisAgentLivenessStore : IAgentLivenessStore
{
    private readonly IConnectionMultiplexer _mux;
    private readonly string _keyPrefix;

    public RedisAgentLivenessStore(IConnectionMultiplexer mux, string keyPrefix)
    {
        ArgumentNullException.ThrowIfNull(mux);
        ArgumentNullException.ThrowIfNull(keyPrefix);
        _mux = mux;
        _keyPrefix = keyPrefix;
    }

    private IDatabase GetDatabase() => _mux.GetDatabase();

    private RedisKey Key(TenantId tenantId, EntityId agentId) =>
        $"{_keyPrefix}presence:agent:{tenantId.Value}:{agentId.Value}";

    public async Task TouchAsync(TenantId tenantId, EntityId agentId, TimeSpan ttl, string nodeId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entry = new AgentLivenessEntry(nodeId, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(entry, AgentLivenessJsonContext.Default.AgentLivenessEntry);
        await GetDatabase().StringSetAsync(Key(tenantId, agentId), json, ttl).ConfigureAwait(false);
    }

    public async Task<bool> IsAliveAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await GetDatabase().KeyExistsAsync(Key(tenantId, agentId)).ConfigureAwait(false);
    }

    public async Task RemoveAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await GetDatabase().KeyDeleteAsync(Key(tenantId, agentId)).ConfigureAwait(false);
    }
}
