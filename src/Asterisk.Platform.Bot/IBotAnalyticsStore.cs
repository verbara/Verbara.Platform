using Asterisk.Platform.Core;

namespace Asterisk.Platform.Bot;

/// <summary>
/// Persists and aggregates bot analytics records.
/// </summary>
public interface IBotAnalyticsStore
{
    Task RecordEventAsync(TenantId tenantId, BotAnalyticsRecord record, CancellationToken ct);
    Task<BotAnalyticsSummary> GetSummaryAsync(TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct);
}
