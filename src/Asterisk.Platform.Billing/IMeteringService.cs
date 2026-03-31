using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Records consumption events for billing and metering purposes.
/// </summary>
public interface IMeteringService
{
    /// <summary>Records a single usage event.</summary>
    Task RecordUsageAsync(TenantId tenantId, UsageType type, decimal quantity, UsageUnit unit, string? channel, string? referenceId, CancellationToken ct);

    /// <summary>Records a batch of pre-built usage records.</summary>
    Task RecordBatchAsync(IReadOnlyList<UsageRecord> records, CancellationToken ct);

    /// <summary>Returns aggregated summaries for the current billing period (calendar month).</summary>
    Task<IReadOnlyList<UsageSummary>> GetCurrentPeriodSummaryAsync(TenantId tenantId, CancellationToken ct);
}
