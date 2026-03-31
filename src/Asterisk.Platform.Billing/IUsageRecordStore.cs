using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Persistence contract for usage records and aggregated summaries.
/// </summary>
public interface IUsageRecordStore
{
    /// <summary>Persists a single usage record.</summary>
    Task SaveAsync(UsageRecord record, CancellationToken ct);

    /// <summary>Persists a batch of usage records.</summary>
    Task SaveBatchAsync(IReadOnlyList<UsageRecord> records, CancellationToken ct);

    /// <summary>Returns aggregated summaries for a tenant within a date range, grouped by UsageType.</summary>
    Task<IReadOnlyList<UsageSummary>> GetSummaryAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset until, CancellationToken ct);

    /// <summary>Returns the aggregated summary for a specific usage type within a date range.</summary>
    Task<UsageSummary?> GetSummaryByTypeAsync(TenantId tenantId, UsageType type, DateTimeOffset from, DateTimeOffset until, CancellationToken ct);
}
