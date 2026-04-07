using System.Collections.Concurrent;
using Asterisk.Sdk.Pro.EventStore;

namespace Asterisk.Platform.Storage.InMemory;

public sealed class InMemoryCompletedSessionStore : ICompletedSessionStore
{
    private readonly ConcurrentDictionary<(string TenantId, string SessionId), CompletedSessionRow> _rows = new();

    public ValueTask UpsertAsync(CompletedSessionRow row, CancellationToken ct = default)
    {
        _rows[(row.TenantId, row.SessionId)] = row;
        return default;
    }

    public ValueTask<CompletedSessionRow?> GetAsync(string tenantId, string sessionId, CancellationToken ct = default)
    {
        _rows.TryGetValue((tenantId, sessionId), out var row);
        return new(row);
    }

    public ValueTask<IReadOnlyList<CompletedSessionRow>> QueryAsync(string tenantId, CompletedSessionQuery query, CancellationToken ct = default)
    {
        var results = _rows.Values
            .Where(r => r.TenantId == tenantId)
            .Where(r => query.From is null || r.StartedAt >= query.From)
            .Where(r => query.To is null || r.StartedAt <= query.To)
            .Where(r => query.ServerId is null || r.ServerId == query.ServerId)
            .Where(r => query.AgentId is null || r.AgentId == query.AgentId)
            .Where(r => query.QueueName is null || r.QueueName == query.QueueName)
            .Where(r => query.Direction is null || r.Direction == query.Direction)
            .OrderByDescending(r => r.StartedAt)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToList();
        return new(results);
    }

    public ValueTask<CompletedSessionStats> GetStatsAsync(
        string tenantId, DateTimeOffset from, DateTimeOffset until, string? serverId = null, CancellationToken ct = default)
    {
        var rows = _rows.Values
            .Where(r => r.TenantId == tenantId && r.StartedAt >= from && r.StartedAt < until)
            .Where(r => serverId is null || r.ServerId == serverId)
            .ToList();

        if (rows.Count == 0)
            return new(new CompletedSessionStats(0, 0, 0, 0, 0, 0, 0));

        var answered = rows.Count(r => r.ConnectedAt is not null);
        var stats = new CompletedSessionStats(
            TotalSessions: rows.Count,
            AnsweredSessions: answered,
            FailedSessions: rows.Count - answered,
            AvgDurationMs: rows.Average(r => r.DurationMs),
            AvgTalkTimeMs: rows.Average(r => r.TalkTimeMs ?? 0),
            AvgWaitTimeMs: rows.Average(r => r.WaitTimeMs ?? 0),
            AvgHoldTimeMs: rows.Average(r => r.HoldTimeMs));
        return new(stats);
    }
}
