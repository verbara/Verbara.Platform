using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

public sealed class DefaultMeteringService : IMeteringService
{
    private readonly IUsageRecordStore _store;
    private readonly IClock _clock;

    public DefaultMeteringService(IUsageRecordStore store, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        _store = store;
        _clock = clock;
    }

    public Task RecordUsageAsync(TenantId tenantId, UsageType type, decimal quantity, UsageUnit unit, string? channel, string? referenceId, CancellationToken ct)
    {
        var record = new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = tenantId,
            UsageType = type,
            Quantity = quantity,
            Unit = unit,
            Channel = channel,
            ReferenceId = referenceId,
            RecordedAt = _clock.UtcNow,
        };

        return _store.SaveAsync(record, ct);
    }

    public Task RecordBatchAsync(IReadOnlyList<UsageRecord> records, CancellationToken ct)
    {
        return _store.SaveBatchAsync(records, ct);
    }

    public Task<IReadOnlyList<UsageSummary>> GetCurrentPeriodSummaryAsync(TenantId tenantId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var periodStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = periodStart.AddMonths(1);

        return _store.GetSummaryAsync(tenantId, periodStart, periodEnd, ct);
    }
}
