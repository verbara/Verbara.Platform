using Verbara.Platform.Core;
using Verbara.Platform.Llm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Billing;

/// <summary>
/// One-time, config-gated current-period back-fill seed for the AI-credit ledger cutover (ADR-0033 addendum,
/// change b, task B7). The ratio basis that converts <c>usage_records</c> tokens into AI credits lives only in
/// <see cref="PlatformLlmOptions"/> app config — unreachable from raw SQL — so the back-fill is a hosted service,
/// not a migration. It runs <b>once</b> (no loop) only when <see cref="PlatformLlmOptions.RunLedgerBackfill"/> is
/// on: for each tenant carrying an <see cref="TenantQuota.AiCreditsMonthly"/> allowance it (1) posts the
/// current-period <see cref="CreditSource.Subscription"/> grant (idempotent on the period key — overlaps harmlessly
/// with <see cref="CreditGrantMintWorker"/>) and (2) seeds that period's already-realised consumption via
/// <see cref="ICreditLedgerStore.PostBackfillConsumptionAsync"/> (covered drawn from the grant + a billable PostPaid
/// tail; idempotent on a <c>backfill:{periodKey}</c> marker). Consumption is reconstructed from
/// <c>usage_records</c> on the <b>same</b> credit basis as the quota / meter / invoice — flat
/// <see cref="PlatformLlmOptions.CreditTokenRatio"/>, or per-direction
/// <see cref="PlatformLlmOptions.InputCreditTokenRatio"/>/<see cref="PlatformLlmOptions.OutputCreditTokenRatio"/>
/// when both are set. A per-tenant failure is logged and skipped; the rest still seed. The operator flips the flag
/// on for the single cutover deploy and off afterwards; a re-run with it left on is a safe no-op.
/// </summary>
public sealed partial class CreditLedgerBackfillService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CreditLedgerBackfillService> _logger;
    private readonly PlatformLlmOptions _options;

    public CreditLedgerBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<CreditLedgerBackfillService> logger,
        IOptions<PlatformLlmOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Gated and one-shot: when the cutover seed switch is off this hosted service does nothing.
        if (!_options.RunLedgerBackfill)
            return;

        try
        {
            await RunBackfillAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping mid-back-fill. The seed is idempotent; the next gated deploy resumes.
        }
        catch (Exception ex)
        {
            LogBackfillFailed(_logger, ex);
        }
    }

    /// <summary>
    /// Runs the back-fill pass once: seeds the current-period grant and the idempotent consumption debit for every
    /// AI-credit tenant. Exposed for tests to drive directly without the host.
    /// </summary>
    internal async Task RunBackfillAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var quotaStore = sp.GetRequiredService<ITenantQuotaStore>();
        var ledger = sp.GetRequiredService<ICreditLedgerStore>();
        var usageStore = sp.GetRequiredService<IUsageRecordStore>();
        var clock = sp.GetRequiredService<IClock>();

        var period = BillingPeriod.Current(clock);
        var now = clock.UtcNow;

        // Same credit basis as the quota / meter / invoice: flat ratio floored at 1, per-direction only when BOTH
        // optional ratios are present and positive.
        var creditRatio = Math.Max(1, _options.CreditTokenRatio);
        var perDirection = _options.InputCreditTokenRatio is > 0 && _options.OutputCreditTokenRatio is > 0;

        var quotas = await quotaStore.ListWithAiCreditsAsync(ct);
        LogBackfillStarted(_logger, quotas.Count, period.Key);

        foreach (var quota in quotas)
        {
            if (quota.AiCreditsMonthly is not { } allowance)
                continue;

            try
            {
                // Reconstruct this period's consumed credits on the exact basis the runtime call-sites use.
                decimal consumed;
                if (perDirection)
                {
                    var bd = await usageStore.GetAiTokenBreakdownAsync(
                        quota.TenantId, UsageType.AiAnalysis, period.Start, period.End, ct);
                    consumed = bd.InputTokens / _options.InputCreditTokenRatio!.Value
                        + bd.OutputTokens / _options.OutputCreditTokenRatio!.Value
                        + bd.UnsplitTokens / creditRatio;
                }
                else
                {
                    var summary = await usageStore.GetSummaryByTypeAsync(
                        quota.TenantId, UsageType.AiAnalysis, period.Start, period.End, ct);
                    consumed = (summary?.TotalQuantity ?? 0m) / creditRatio;
                }

                // Seed the current-period Subscription grant (idempotent on the period key — safe even if the mint
                // worker already minted it). The back-fill owns the seed; mint overlap is a no-op.
                await ledger.PostGrantAsync(
                    new CreditLedgerEntry
                    {
                        EntryId = EntityId.New(),
                        TenantId = quota.TenantId,
                        EntryType = CreditEntryType.Grant,
                        Source = CreditSource.Subscription,
                        Amount = allowance,
                        PeriodKey = period.Key,
                        ExpiresAt = period.End,
                        CreatedAt = now,
                    },
                    ct);

                // Seed the already-realised consumption (covered draw + PostPaid tail), idempotent on the period marker.
                await ledger.PostBackfillConsumptionAsync(quota.TenantId, consumed, period.Key, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTenantBackfillFailed(_logger, quota.TenantId.Value, ex);
            }
        }

        LogBackfillCompleted(_logger, quotas.Count, period.Key);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "AI-credit ledger back-fill starting for {TenantCount} tenant(s), period {PeriodKey}")]
    private static partial void LogBackfillStarted(ILogger logger, int tenantCount, string periodKey);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "AI-credit ledger back-fill completed for {TenantCount} tenant(s), period {PeriodKey}")]
    private static partial void LogBackfillCompleted(ILogger logger, int tenantCount, string periodKey);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "AI-credit ledger back-fill failed for tenant {TenantId} — skipping")]
    private static partial void LogTenantBackfillFailed(ILogger logger, string tenantId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "AI-credit ledger back-fill pass failed")]
    private static partial void LogBackfillFailed(ILogger logger, Exception ex);
}
