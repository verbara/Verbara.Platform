using Microsoft.Extensions.Options;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;

namespace Verbara.Platform.Billing;

public sealed class DefaultQuotaEnforcementService : IQuotaEnforcementService
{
    private readonly ITenantQuotaStore _quotaStore;
    private readonly IUsageRecordStore _usageStore;
    private readonly IClock _clock;
    private readonly long _creditTokenRatio;
    private readonly long? _inputRatio;
    private readonly long? _outputRatio;

    public DefaultQuotaEnforcementService(ITenantQuotaStore quotaStore, IUsageRecordStore usageStore, IClock clock, IOptions<PlatformLlmOptions> platformOptions)
    {
        ArgumentNullException.ThrowIfNull(quotaStore);
        ArgumentNullException.ThrowIfNull(usageStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(platformOptions);
        _quotaStore = quotaStore;
        _usageStore = usageStore;
        _clock = clock;
        _creditTokenRatio = Math.Max(1, platformOptions.Value.CreditTokenRatio);
        _inputRatio = platformOptions.Value.InputCreditTokenRatio;
        _outputRatio = platformOptions.Value.OutputCreditTokenRatio;
    }

    /// <summary>Per-direction (input/output) credit pricing is active only when BOTH ratios are set and &gt; 0.</summary>
    private bool PerDirectionActive => _inputRatio is > 0 && _outputRatio is > 0;

    public async Task<QuotaCheckResult> CheckQuotaAsync(TenantId tenantId, UsageType type, decimal additionalQuantity, CancellationToken ct)
    {
        if (type == UsageType.AiAnalysis && PerDirectionActive)
            return await CheckAiCreditQuotaPerDirectionAsync(tenantId, type, additionalQuantity, ct);

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

    // typification-llm-inout-pricing — differentiated AI-Credit aggregation (opt-in). Works in CREDITS:
    // currentCredits = Σ(input/inRatio + output/outRatio) over split records + Σ(quantity/flatRatio) over unsplit,
    // decomposed via the store's single GetAiTokenBreakdownAsync aggregation. The limit is AiCreditsMonthly
    // directly (credits, NOT multiplied by a ratio). additionalQuantity (nominal tokens) → credits via the flat ratio.
    private async Task<QuotaCheckResult> CheckAiCreditQuotaPerDirectionAsync(TenantId tenantId, UsageType type, decimal additionalQuantity, CancellationToken ct)
    {
        var quota = await _quotaStore.GetAsync(tenantId, ct);
        if (quota is null)
            return new QuotaCheckResult(true, null, 0);

        if (quota.AiCreditsMonthly is not { } creditsLimit)
            return new QuotaCheckResult(true, null, 0); // null = unlimited / pay-as-you-go

        var (periodStart, periodEnd) = GetCurrentPeriod();
        var bd = await _usageStore.GetAiTokenBreakdownAsync(tenantId, type, periodStart, periodEnd, ct);

        var currentCredits = bd.InputTokens / _inputRatio!.Value
            + bd.OutputTokens / _outputRatio!.Value
            + bd.UnsplitTokens / _creditTokenRatio;
        var additionalCredits = additionalQuantity / _creditTokenRatio;
        var projectedCredits = currentCredits + additionalCredits;
        var usagePercent = (double)(projectedCredits / creditsLimit * 100m);

        if (projectedCredits <= creditsLimit)
            return new QuotaCheckResult(true, null, usagePercent);

        var reason = $"{type} credit quota exceeded: {projectedCredits:F2}/{creditsLimit} ({usagePercent:F1}%)";

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

    private long? GetLimitForType(TenantQuota quota, UsageType type) => type switch
    {
        UsageType.VoiceInbound or UsageType.VoiceOutbound => quota.MaxMonthlyVoiceMinutes,
        UsageType.SmsInbound or UsageType.SmsOutbound or
        UsageType.WhatsAppInbound or UsageType.WhatsAppOutbound or
        UsageType.EmailInbound or UsageType.EmailOutbound or
        UsageType.TelegramInbound or UsageType.TelegramOutbound => quota.MaxMonthlyMessages,
        UsageType.RecordingStorage or UsageType.MediaStorage => quota.MaxStorageBytes,
        UsageType.AiAnalysis => quota.AiCreditsMonthly is { } c ? c * _creditTokenRatio : null, // credits → token-equiv
        _ => null,
    };
}
