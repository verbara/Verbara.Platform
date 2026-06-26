using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Notifications;
using Verbara.Platform.Llm;
using Verbara.Platform.Typification.Ai;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// <see cref="ITypificationCreditMeter"/> backed by the Billing <see cref="IMeteringService"/>.
/// Records platform-managed LLM token usage as a single <c>AiAnalysis</c>/<c>Tokens</c>
/// <see cref="UsageRecord"/> carrying input/output token counts + model in metadata
/// (via <c>RecordBatchAsync</c> — the metering API that preserves <c>Metadata</c>).
/// After recording, when the tenant has a non-null <see cref="TenantQuota.AiCreditsMonthly"/>
/// allowance, it dispatches a one-shot threshold notification (80 % warning / 100 % critical)
/// on the FIRST crossing, using stateless straddle detection
/// (<c>previousCredits &lt; thresholdCredits ≤ currentCredits</c>) — no per-tenant state store.
/// All credit math mirrors <see cref="DefaultQuotaEnforcementService"/>. Notification dispatch
/// is best-effort: any failure is swallowed (logged) so it can never break metering.
/// </summary>
internal sealed partial class BillingTypificationCreditMeter(
    IMeteringService metering,
    IClock clock,
    IUsageRecordStore usageStore,
    ITenantQuotaStore quotaStore,
    INotificationService notifications,
    IOptions<PlatformLlmOptions> platformOptions) : ITypificationCreditMeter
{
    private const decimal WarningFraction = 0.8m;

    private readonly IMeteringService _metering = metering ?? throw new ArgumentNullException(nameof(metering));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IUsageRecordStore _usageStore = usageStore ?? throw new ArgumentNullException(nameof(usageStore));
    private readonly ITenantQuotaStore _quotaStore = quotaStore ?? throw new ArgumentNullException(nameof(quotaStore));
    private readonly INotificationService _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
    private readonly ILogger<BillingTypificationCreditMeter>? _logger;

    private readonly long _creditTokenRatio = Math.Max(1, platformOptions.Value.CreditTokenRatio);
    private readonly long? _inputRatio = platformOptions.Value.InputCreditTokenRatio;
    private readonly long? _outputRatio = platformOptions.Value.OutputCreditTokenRatio;

    public BillingTypificationCreditMeter(
        IMeteringService metering,
        IClock clock,
        IUsageRecordStore usageStore,
        ITenantQuotaStore quotaStore,
        INotificationService notifications,
        IOptions<PlatformLlmOptions> platformOptions,
        ILogger<BillingTypificationCreditMeter> logger)
        : this(metering, clock, usageStore, quotaStore, notifications, platformOptions)
        => _logger = logger;

    /// <summary>Per-direction (input/output) credit pricing is active only when BOTH ratios are set and &gt; 0.</summary>
    private bool PerDirectionActive => _inputRatio is > 0 && _outputRatio is > 0;

    public async Task RecordAsync(TenantId tenantId, string conversationId, int promptTokens, int completionTokens, int totalTokens, string model, CancellationToken ct)
    {
        if (totalTokens <= 0)
            return;

        var record = new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = tenantId,
            UsageType = UsageType.AiAnalysis,
            Quantity = totalTokens,
            Unit = UsageUnit.Tokens,
            Channel = null,
            ReferenceId = conversationId,
            RecordedAt = _clock.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["inputTokens"] = promptTokens.ToString(CultureInfo.InvariantCulture),
                ["outputTokens"] = completionTokens.ToString(CultureInfo.InvariantCulture),
                ["model"] = model,
            },
        };
        await _metering.RecordBatchAsync(new[] { record }, ct).ConfigureAwait(false);

        // Best-effort threshold notification — MUST NEVER break metering.
        try
        {
            await DispatchThresholdNotificationsAsync(tenantId, promptTokens, completionTokens, totalTokens, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger is not null)
                ThresholdNotificationFailed(_logger, tenantId.Value, ex);
        }
    }

    /// <summary>
    /// Stateless first-crossing threshold detection. Computes the post-record period credit total
    /// (<c>currentCredits</c>) and <c>previousCredits = currentCredits − thisRecordCredits</c>; fires
    /// the warning (80 %) / critical (100 %) notification only on the period's first straddle of each
    /// threshold. No-op when the tenant has no credit allowance (unlimited / pay-as-you-go).
    /// </summary>
    private async Task DispatchThresholdNotificationsAsync(TenantId tenantId, int promptTokens, int completionTokens, int totalTokens, CancellationToken ct)
    {
        var quota = await _quotaStore.GetAsync(tenantId, ct).ConfigureAwait(false);
        if (quota?.AiCreditsMonthly is not { } allowance || allowance <= 0)
            return;

        var (periodStart, periodEnd) = GetCurrentPeriod();
        var currentCredits = await GetCurrentPeriodCreditsAsync(tenantId, periodStart, periodEnd, ct).ConfigureAwait(false);
        var thisRecordCredits = CreditsForRecord(promptTokens, completionTokens, totalTokens);
        var previousCredits = currentCredits - thisRecordCredits;

        var warningThreshold = WarningFraction * allowance;
        decimal criticalThreshold = allowance;

        if (Straddles(previousCredits, currentCredits, warningThreshold))
        {
            await _notifications.CreateAsync(
                tenantId.Value,
                "billing.quota_warning",
                "AI credit usage at 80%",
                $"AI credit usage has reached {currentCredits:F0} of {allowance} credits (80% of your monthly allowance).",
                actionUrl: null,
                ct).ConfigureAwait(false);
        }

        if (Straddles(previousCredits, currentCredits, criticalThreshold))
        {
            await _notifications.CreateAsync(
                tenantId.Value,
                "billing.quota_exceeded",
                "AI credit allowance exhausted",
                $"AI credit usage has reached {currentCredits:F0} of {allowance} credits (100% of your monthly allowance).",
                actionUrl: null,
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>First crossing: previous strictly below the threshold, current at or above it.</summary>
    private static bool Straddles(decimal previous, decimal current, decimal threshold)
        => previous < threshold && threshold <= current;

    /// <summary>
    /// Post-record period credit total on the same basis as <see cref="DefaultQuotaEnforcementService"/>:
    /// per-direction (<c>input/inRatio + output/outRatio + unsplit/flatRatio</c>) when both direction ratios
    /// are active, otherwise flat (<c>Σtokens / CreditTokenRatio</c>).
    /// </summary>
    private async Task<decimal> GetCurrentPeriodCreditsAsync(TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
    {
        if (PerDirectionActive)
        {
            var bd = await _usageStore.GetAiTokenBreakdownAsync(tenantId, UsageType.AiAnalysis, periodStart, periodEnd, ct).ConfigureAwait(false);
            return bd.InputTokens / _inputRatio!.Value
                + bd.OutputTokens / _outputRatio!.Value
                + bd.UnsplitTokens / _creditTokenRatio;
        }

        var summary = await _usageStore.GetSummaryByTypeAsync(tenantId, UsageType.AiAnalysis, periodStart, periodEnd, ct).ConfigureAwait(false);
        var totalTokens = summary?.TotalQuantity ?? 0m;
        return totalTokens / _creditTokenRatio;
    }

    /// <summary>Credits contributed by this single record, on the active basis (per-direction or flat).</summary>
    private decimal CreditsForRecord(int promptTokens, int completionTokens, int totalTokens)
        => PerDirectionActive
            ? (decimal)promptTokens / _inputRatio!.Value + (decimal)completionTokens / _outputRatio!.Value
            : (decimal)totalTokens / _creditTokenRatio;

    private (DateTimeOffset Start, DateTimeOffset End) GetCurrentPeriod()
    {
        var now = _clock.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return (start, start.AddMonths(1));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI-credit threshold notification dispatch failed for tenant {TenantId}; metering already recorded.")]
    private static partial void ThresholdNotificationFailed(ILogger logger, string tenantId, Exception exception);
}
