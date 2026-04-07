using System.Collections.Concurrent;
using Asterisk.Sdk.Pro.Analytics;

namespace Asterisk.Platform.Storage.InMemory;

public sealed class InMemoryIntervalSnapshotStore : IIntervalSnapshotStore
{
    private readonly ConcurrentDictionary<(string TenantId, string QueueName, string ServerId, DateTimeOffset IntervalStart), IntervalSnapshot> _intervals = new();
    private readonly ConcurrentDictionary<(string TenantId, string AgentId, string ServerId, DateTimeOffset IntervalStart), AgentSnapshot> _agents = new();
    private readonly ConcurrentDictionary<(string TenantId, long CampaignId, string ServerId, DateTimeOffset IntervalStart), CampaignSnapshot> _campaigns = new();

    public ValueTask UpsertAsync(IntervalSnapshot snapshot, CancellationToken ct = default)
    {
        _intervals[(snapshot.TenantId, snapshot.QueueName, snapshot.ServerId, snapshot.IntervalStart)] = snapshot;
        return default;
    }

    public ValueTask UpsertAgentAsync(AgentSnapshot snapshot, CancellationToken ct = default)
    {
        _agents[(snapshot.TenantId, snapshot.AgentId, snapshot.ServerId, snapshot.IntervalStart)] = snapshot;
        return default;
    }

    public ValueTask UpsertCampaignAsync(CampaignSnapshot snapshot, CancellationToken ct = default)
    {
        _campaigns[(snapshot.TenantId, snapshot.CampaignId, snapshot.ServerId, snapshot.IntervalStart)] = snapshot;
        return default;
    }

    public ValueTask<IReadOnlyList<IntervalSnapshot>> QueryAsync(
        string tenantId, DateTimeOffset from, DateTimeOffset until,
        string? queueName = null, string? serverId = null,
        CancellationToken ct = default)
    {
        var results = _intervals.Values
            .Where(s => s.TenantId == tenantId && s.IntervalStart >= from && s.IntervalStart < until)
            .Where(s => queueName is null || s.QueueName == queueName)
            .Where(s => serverId is null || s.ServerId == serverId)
            .OrderByDescending(s => s.IntervalStart)
            .ToList();
        return new(results);
    }

    public ValueTask<IReadOnlyList<AgentSnapshot>> QueryAgentAsync(
        string tenantId, DateTimeOffset from, DateTimeOffset until,
        string? agentId = null,
        CancellationToken ct = default)
    {
        var results = _agents.Values
            .Where(s => s.TenantId == tenantId && s.IntervalStart >= from && s.IntervalStart < until)
            .Where(s => agentId is null || s.AgentId == agentId)
            .OrderByDescending(s => s.IntervalStart)
            .ToList();
        return new(results);
    }
}
