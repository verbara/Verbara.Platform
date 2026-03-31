using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

public sealed class DefaultQuotaEnforcementService : IQuotaEnforcementService
{
    private readonly ITenantQuotaStore _quotaStore;
    private readonly IUsageRecordStore _usageStore;
    private readonly IClock _clock;

    public DefaultQuotaEnforcementService(ITenantQuotaStore quotaStore, IUsageRecordStore usageStore, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(quotaStore);
        ArgumentNullException.ThrowIfNull(usageStore);
        ArgumentNullException.ThrowIfNull(clock);
        _quotaStore = quotaStore;
        _usageStore = usageStore;
        _clock = clock;
    }

    public async Task<QuotaCheckResult> CheckQuotaAsync(TenantId tenantId, UsageType type, decimal additionalQuantity, CancellationToken ct)
    {
        var quota = await _quotaStore.GetAsync(tenantId, ct);
        if (quota is null)
            return new QuotaCheckResult(true, null, 0);

        var limit = GetLimitForType(quota, type);
        if (limit is null)
            return new QuotaCheckResult(true, null, 0);

        var (periodStart, periodEnd) = GetCurrentPeriod();
        var summary = await _usageStore.GetSummaryByTypeAsync(tenantId, type, periodStart, periodEnd, ct);
        var currentUsage = summary?.TotalQuantity ?? 0m;
        var projectedUsage = currentUsage + additionalQuantity;
        var usagePercent = (double)(projectedUsage / limit.Value * 100m);

        if (projectedUsage <= limit.Value)
            return new QuotaCheckResult(true, null, usagePercent);

        var reason = $"{type} quota exceeded: {projectedUsage:F1}/{limit.Value} ({usagePercent:F1}%)";

        return quota.QuotaAction switch
        {
            QuotaAction.Warn => new QuotaCheckResult(true, reason, usagePercent),
            QuotaAction.SoftBlock => new QuotaCheckResult(false, reason, usagePercent),
            QuotaAction.HardBlock => new QuotaCheckResult(false, reason, usagePercent),
            _ => new QuotaCheckResult(true, reason, usagePercent),
        };
    }

    public async Task<TenantQuotaStatus> GetQuotaStatusAsync(TenantId tenantId, CancellationToken ct)
    {
        var quota = await _quotaStore.GetAsync(tenantId, ct);
        var (periodStart, periodEnd) = GetCurrentPeriod();
        var summaries = await _usageStore.GetSummaryAsync(tenantId, periodStart, periodEnd, ct);

        return new TenantQuotaStatus(tenantId, quota, summaries);
    }

    private (DateTimeOffset Start, DateTimeOffset End) GetCurrentPeriod()
    {
        var now = _clock.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return (start, start.AddMonths(1));
    }

    private static long? GetLimitForType(TenantQuota quota, UsageType type) => type switch
    {
        UsageType.VoiceInbound or UsageType.VoiceOutbound => quota.MaxMonthlyVoiceMinutes,
        UsageType.SmsInbound or UsageType.SmsOutbound or
        UsageType.WhatsAppInbound or UsageType.WhatsAppOutbound or
        UsageType.EmailInbound or UsageType.EmailOutbound or
        UsageType.TelegramInbound or UsageType.TelegramOutbound => quota.MaxMonthlyMessages,
        UsageType.RecordingStorage or UsageType.MediaStorage => quota.MaxStorageBytes,
        _ => null,
    };
}
