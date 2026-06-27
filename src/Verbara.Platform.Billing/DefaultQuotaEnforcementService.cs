using Microsoft.Extensions.Options;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;

namespace Verbara.Platform.Billing;

public sealed class DefaultQuotaEnforcementService : IQuotaEnforcementService
{
    private readonly ITenantQuotaStore _quotaStore;
    private readonly IUsageRecordStore _usageStore;
    private readonly IClock _clock;
    private readonly ICreditLedgerStore _ledger;
    private readonly long _creditTokenRatio;
    private readonly long? _inputRatio;
    private readonly long? _outputRatio;
    private readonly bool _ledgerEnforcementEnabled;

    public DefaultQuotaEnforcementService(ITenantQuotaStore quotaStore, IUsageRecordStore usageStore, IClock clock, ICreditLedgerStore ledger, IOptions<PlatformLlmOptions> platformOptions)
    {
        ArgumentNullException.ThrowIfNull(quotaStore);
        ArgumentNullException.ThrowIfNull(usageStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(platformOptions);
        _quotaStore = quotaStore;
        _usageStore = usageStore;
        _clock = clock;
        _ledger = ledger;
        _creditTokenRatio = Math.Max(1, platformOptions.Value.CreditTokenRatio);
        _inputRatio = platformOptions.Value.InputCreditTokenRatio;
        _outputRatio = platformOptions.Value.OutputCreditTokenRatio;
        _ledgerEnforcementEnabled = platformOptions.Value.LedgerEnforcementEnabled;
    }

    /// <summary>Per-direction (input/output) credit pricing is active only when BOTH ratios are set and &gt; 0.</summary>
    private bool PerDirectionActive => _inputRatio is > 0 && _outputRatio is > 0;

    public async Task<QuotaCheckResult> CheckQuotaAsync(TenantId tenantId, UsageType type, decimal additionalQuantity, CancellationToken ct)
    {
        var quota = await _quotaStore.GetAsync(tenantId, ct);

        // Credit-ledger cutover (ADR-0033 addendum, Model C). When the kill-switch is on, the AiAnalysis check
        // reads the prepaid balance from the ledger projection and decides on a single credit-denominated basis
        // (no flat/per-direction split — the balance is already in credits). Default-off: the legacy two-branch
        // dispatch below runs unchanged, preserving the change-(a) characterization outputs byte-for-byte.
        if (type == UsageType.AiAnalysis && _ledgerEnforcementEnabled)
            return await CheckAiCreditQuotaLedgerAsync(tenantId, quota, additionalQuantity, ct);

        if (type == UsageType.AiAnalysis && PerDirectionActive)
            return await CheckAiCreditQuotaPerDirectionAsync(tenantId, quota, type, additionalQuantity, ct);

        if (quota is null)
            return new QuotaCheckResult(true, null, 0, QuotaOutcome.Allow);

        var limit = GetLimitForType(quota, type);
        if (limit is null)
            return new QuotaCheckResult(true, null, 0, QuotaOutcome.Allow);

        var (periodStart, periodEnd, _) = BillingPeriod.Current(_clock);
        var summary = await _usageStore.GetSummaryByTypeAsync(tenantId, type, periodStart, periodEnd, ct);
        var currentUsage = summary?.TotalQuantity ?? 0m;
        var projectedUsage = currentUsage + additionalQuantity;
        var usagePercent = (double)(projectedUsage / limit.Value * 100m);

        if (projectedUsage <= limit.Value)
            return new QuotaCheckResult(true, null, usagePercent, QuotaOutcome.Allow);

        var reason = $"{type} quota exceeded: {projectedUsage:F1}/{limit.Value} ({usagePercent:F1}%)";

        return quota.QuotaAction switch
        {
            QuotaAction.Warn => new QuotaCheckResult(true, reason, usagePercent, QuotaOutcome.Warn),
            QuotaAction.SoftBlock => new QuotaCheckResult(false, reason, usagePercent, QuotaOutcome.SoftBlock),
            QuotaAction.HardBlock => new QuotaCheckResult(false, reason, usagePercent, QuotaOutcome.HardBlock),
            _ => new QuotaCheckResult(true, reason, usagePercent, QuotaOutcome.Warn),
        };
    }

    // Credit-ledger cutover (ADR-0033 addendum, Model C — postpaid). UNIFIED credit-denominated path: the
    // prepaid balance is read from the O(1) ledger projection (already in credits), so there is no flat vs.
    // per-direction split here. Exhaustion is a stock test — can the remaining prepaid balance cover THIS
    // debit? — not a flow comparison against an aggregated consumption. A Warn tenant is never hard-blocked at
    // zero: it surfaces Warn (Allowed) and the metering funnel overflows the excess into the billable PostPaid
    // tail (preserving #4's postpaid overage).
    private async Task<QuotaCheckResult> CheckAiCreditQuotaLedgerAsync(TenantId tenantId, TenantQuota? quota, decimal additionalQuantity, CancellationToken ct)
    {
        if (quota?.AiCreditsMonthly is not { } allowanceLong)
            return new QuotaCheckResult(true, null, 0, QuotaOutcome.Allow); // null = unlimited / pay-as-you-go — never blocks

        var limit = (decimal)allowanceLong;
        var balance = await _ledger.GetBalanceAsync(tenantId, ct);
        var additionalCredits = additionalQuantity / _creditTokenRatio;
        var consumed = Math.Max(0m, limit - balance);
        var projected = consumed + additionalCredits;
        var usagePercent = limit > 0m ? (double)(projected / limit * 100m) : 0d;

        // Exhausted when the prepaid stock cannot cover this debit (boundary inclusive: balance == debit serves).
        if (balance >= additionalCredits)
            return new QuotaCheckResult(true, null, usagePercent, QuotaOutcome.Allow);

        var reason = $"{UsageType.AiAnalysis} credit quota exceeded: {projected:F2}/{limit} ({usagePercent:F1}%)";

        return quota.QuotaAction switch
        {
            QuotaAction.Warn => new QuotaCheckResult(true, reason, usagePercent, QuotaOutcome.Warn),
            QuotaAction.SoftBlock => new QuotaCheckResult(false, reason, usagePercent, QuotaOutcome.SoftBlock),
            QuotaAction.HardBlock => new QuotaCheckResult(false, reason, usagePercent, QuotaOutcome.HardBlock),
            _ => new QuotaCheckResult(true, reason, usagePercent, QuotaOutcome.Warn),
        };
    }

    // typification-llm-inout-pricing — differentiated AI-Credit aggregation (opt-in). Works in CREDITS:
    // currentCredits = Σ(input/inRatio + output/outRatio) over split records + Σ(quantity/flatRatio) over unsplit,
    // decomposed via the store's single GetAiTokenBreakdownAsync aggregation. The limit is AiCreditsMonthly
    // directly (credits, NOT multiplied by a ratio). additionalQuantity (nominal tokens) → credits via the flat ratio.
    private async Task<QuotaCheckResult> CheckAiCreditQuotaPerDirectionAsync(TenantId tenantId, TenantQuota? quota, UsageType type, decimal additionalQuantity, CancellationToken ct)
    {
        if (quota is null)
            return new QuotaCheckResult(true, null, 0, QuotaOutcome.Allow);

        if (quota.AiCreditsMonthly is not { } creditsLimit)
            return new QuotaCheckResult(true, null, 0, QuotaOutcome.Allow); // null = unlimited / pay-as-you-go

        var (periodStart, periodEnd, _) = BillingPeriod.Current(_clock);
        var bd = await _usageStore.GetAiTokenBreakdownAsync(tenantId, type, periodStart, periodEnd, ct);

        var currentCredits = bd.InputTokens / _inputRatio!.Value
            + bd.OutputTokens / _outputRatio!.Value
            + bd.UnsplitTokens / _creditTokenRatio;
        var additionalCredits = additionalQuantity / _creditTokenRatio;
        var projectedCredits = currentCredits + additionalCredits;
        var usagePercent = (double)(projectedCredits / creditsLimit * 100m);

        if (projectedCredits <= creditsLimit)
            return new QuotaCheckResult(true, null, usagePercent, QuotaOutcome.Allow);

        var reason = $"{type} credit quota exceeded: {projectedCredits:F2}/{creditsLimit} ({usagePercent:F1}%)";

        return quota.QuotaAction switch
        {
            QuotaAction.Warn => new QuotaCheckResult(true, reason, usagePercent, QuotaOutcome.Warn),
            QuotaAction.SoftBlock => new QuotaCheckResult(false, reason, usagePercent, QuotaOutcome.SoftBlock),
            QuotaAction.HardBlock => new QuotaCheckResult(false, reason, usagePercent, QuotaOutcome.HardBlock),
            _ => new QuotaCheckResult(true, reason, usagePercent, QuotaOutcome.Warn),
        };
    }

    public async Task<TenantQuotaStatus> GetQuotaStatusAsync(TenantId tenantId, CancellationToken ct)
    {
        var quota = await _quotaStore.GetAsync(tenantId, ct);
        var (periodStart, periodEnd, _) = BillingPeriod.Current(_clock);
        var summaries = await _usageStore.GetSummaryAsync(tenantId, periodStart, periodEnd, ct);

        return new TenantQuotaStatus(tenantId, quota, summaries);
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
