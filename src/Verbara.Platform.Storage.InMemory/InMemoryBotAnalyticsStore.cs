using System.Collections.Concurrent;
using Verbara.Platform.Bot;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.InMemory;

public sealed class InMemoryBotAnalyticsStore : IBotAnalyticsStore
{
    private readonly ConcurrentBag<(TenantId TenantId, BotAnalyticsRecord Record)> _records = [];

    public Task RecordEventAsync(TenantId tenantId, BotAnalyticsRecord record, CancellationToken ct)
    {
        _records.Add((tenantId, record));
        return Task.CompletedTask;
    }

    public Task<BotAnalyticsSummary> GetSummaryAsync(
        TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
    {
        var filtered = _records
            .Where(r => r.TenantId == tenantId &&
                        r.Record.CreatedAt >= periodStart &&
                        r.Record.CreatedAt <= periodEnd)
            .Select(r => r.Record)
            .ToList();

        var started = filtered.Count(r => r.EventType == nameof(BotAnalyticsEventType.ConversationStarted));
        var handedOff = filtered.Count(r => r.EventType is nameof(BotAnalyticsEventType.HandedOff) or nameof(BotAnalyticsEventType.MaxTurnsReached));
        var resolved = filtered.Count(r => r.EventType == nameof(BotAnalyticsEventType.Resolved));
        var failed = filtered.Count(r => r.EventType == nameof(BotAnalyticsEventType.Failed));
        var total = started > 0 ? started : (handedOff + resolved + failed);

        var avgTurns = filtered.Count > 0 ? filtered.Average(r => r.TurnCount) : 0.0;

        var summary = new BotAnalyticsSummary
        {
            TotalConversations = total,
            HandedOff = handedOff,
            Resolved = resolved,
            Failed = failed,
            HandoffRate = total > 0 ? (double)handedOff / total : 0,
            ResolutionRate = total > 0 ? (double)resolved / total : 0,
            AvgTurns = Math.Round(avgTurns, 1),
            FailureRate = total > 0 ? (double)failed / total : 0,
        };

        return Task.FromResult(summary);
    }
}
