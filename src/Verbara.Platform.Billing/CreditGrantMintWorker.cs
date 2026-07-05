using Verbara.Platform.Core;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Billing;

/// <summary>
/// Recurring subscription-grant mint: ensures every tenant that carries an
/// <see cref="TenantQuota.AiCreditsMonthly"/> allowance has the current-period
/// <see cref="CreditSource.Subscription"/> grant on its AI-credit ledger. Each cycle keys the grant on the
/// canonical <see cref="BillingPeriod"/> <c>"yyyy-MM"</c> period key and posts it via
/// <see cref="ICreditLedgerStore.PostGrantAsync"/>, which is idempotent (<c>ON CONFLICT DO NOTHING</c> on
/// <c>(tenant_id, period_key, entry_type)</c> + conditional projection upsert) — so re-mints within the same
/// period are safe no-ops that neither double-insert nor double-credit. Mirrors
/// <see cref="OverageInvoiceIssuanceWorker"/>'s hosted-service template (keyed resilience policy, a scope per
/// cycle, and an <c>internal</c> per-cycle method so tests drive it without the timer loop).
/// <para>
/// <b>Known month-rollover window (ADR-0033 addendum):</b> a tenant that first consumes after a UTC month
/// boundary but before this worker's next tick (≤ one <see cref="DunningConfig.CheckIntervalHours"/>
/// interval) would see no current-period grant yet — its balance read would return the prior carry-over
/// only. The named fast-follow, <i>lazy-mint-on-read</i>, <b>SHIPPED</b> as
/// <c>credit-grant-lazy-mint-rollover</c>: <see cref="CreditGrantLazyMinter"/> mints the current-period grant
/// inline (reusing this worker's exact <see cref="ICreditLedgerStore.PostGrantAsync"/> posting) on the
/// enforcement (<c>DefaultQuotaEnforcementService</c>) and readout (<c>CreditLedgerEndpoints</c>)
/// balance-read paths, gated by an indexed grant-existence check so steady-state reads stay write-free. This
/// worker remains the steady-state mint; the lazy mint only closes the rollover window.
/// </para>
/// </summary>
public sealed partial class CreditGrantMintWorker : BackgroundService
{
    /// <summary>
    /// Keyed-service name for the per-cycle <see cref="ResiliencePolicy"/> that wraps each mint pass.
    /// Circuit-open skips the current cycle; the next tick retries.
    /// </summary>
    public const string ResiliencePolicyKey = "worker.credit-grant-mint";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CreditGrantMintWorker> _logger;
    private readonly DunningConfig _config;
    private readonly ResiliencePolicy _policy;

    public CreditGrantMintWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<CreditGrantMintWorker> logger,
        IOptions<DunningConfig> config,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config.Value;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _policy.ExecuteAsync(
                        ResiliencePolicyKey,
                        async innerCt =>
                        {
                            await ProcessMintCycleAsync(innerCt);
                            return 0;
                        },
                        stoppingToken);
                }
                catch (CircuitBreakerOpenException)
                {
                    // Circuit open — skip this cycle, next tick retries.
                    LogCircuitOpen(_logger);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogMintCycleFailed(_logger, ex);
                }

                await Task.Delay(TimeSpan.FromHours(_config.CheckIntervalHours), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping. Don't rethrow.
        }
        catch (Exception fatalEx)
        {
            LogWorkerCrash(_logger, nameof(CreditGrantMintWorker), fatalEx.Message, fatalEx);
            throw;
        }
    }

    internal async Task ProcessMintCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var quotaStore = sp.GetRequiredService<ITenantQuotaStore>();
        var ledger = sp.GetRequiredService<ICreditLedgerStore>();
        var clock = sp.GetRequiredService<IClock>();

        var period = BillingPeriod.Current(clock);
        var now = clock.UtcNow;

        var quotas = await quotaStore.ListWithAiCreditsAsync(ct);
        foreach (var quota in quotas)
        {
            if (quota.AiCreditsMonthly is not { } allowance)
                continue;

            try
            {
                // Idempotent on (tenant_id, period_key, entry_type): a re-mint within the same period is a no-op.
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
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogMintFailed(_logger, quota.TenantId.Value, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Credit-grant mint cycle failed")]
    private static partial void LogMintCycleFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to mint subscription credit grant for tenant {TenantId}")]
    private static partial void LogMintFailed(ILogger logger, string tenantId, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Circuit open for worker.credit-grant-mint — skipping cycle")]
    private static partial void LogCircuitOpen(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private static partial void LogWorkerCrash(ILogger logger, string workerName, string reason, Exception ex);
}
