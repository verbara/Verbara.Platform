using Verbara.Platform.Core;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Billing;

/// <summary>
/// Lot-expiry reclaim sweeper: finds every expired non-empty <c>credit_lot</c> across all tenants and reclaims
/// its unconsumed credits idempotently (ADR-0033 / the 2026-06-28 (c2) resolution addendum, BLOCKER
/// resolution 2). Each cycle reads the work-list via <see cref="ICreditLedgerStore.GetExpiredLotsAsync"/> and
/// reclaims each lot via <see cref="ICreditLedgerStore.ReclaimExpiredLotAsync"/>, which — in one transaction
/// with the projection row locked first — posts an offsetting debit tagged the lot's <b>own</b>
/// <see cref="CreditSource"/> equal to the live <see cref="CreditLot.Remaining"/> (carrying
/// <c>external_ref = "lot-expiry:{lotId}"</c> + <c>ON CONFLICT DO NOTHING</c>) and, only if that row inserted,
/// decrements the projection and zeroes the lot. The whole reclaim is gated on the insert, so a re-tick is a
/// complete no-op and can never claw back already-consumed credits. This single mechanism enforces both
/// subscription no-carryover (the period-end Subscription lot) and promo expiry. Mirrors
/// <see cref="CreditGrantMintWorker"/>'s hosted-service template (keyed resilience policy, a scope per cycle,
/// an <c>internal</c> per-cycle method so tests drive it without the timer loop, <see cref="IClock"/> for the
/// expiry comparison, and <c>[LoggerMessage]</c> source-gen logging).
/// </summary>
public sealed partial class CreditLotExpiryReclaimWorker : BackgroundService
{
    /// <summary>
    /// Keyed-service name for the per-cycle <see cref="ResiliencePolicy"/> that wraps each reclaim pass.
    /// Circuit-open skips the current cycle; the next tick retries.
    /// </summary>
    public const string ResiliencePolicyKey = "worker.credit-lot-expiry-reclaim";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CreditLotExpiryReclaimWorker> _logger;
    private readonly DunningConfig _config;
    private readonly ResiliencePolicy _policy;

    public CreditLotExpiryReclaimWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<CreditLotExpiryReclaimWorker> logger,
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
                            await ProcessExpiryCycleAsync(innerCt);
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
                    LogReclaimCycleFailed(_logger, ex);
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
            LogWorkerCrash(_logger, nameof(CreditLotExpiryReclaimWorker), fatalEx.Message, fatalEx);
            throw;
        }
    }

    internal async Task ProcessExpiryCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var ledger = sp.GetRequiredService<ICreditLedgerStore>();
        var clock = sp.GetRequiredService<IClock>();

        var now = clock.UtcNow;

        var expired = await ledger.GetExpiredLotsAsync(now, ct);

        var reclaimedCount = 0;
        var reclaimedTotal = 0m;

        foreach (var lot in expired)
        {
            try
            {
                // Each reclaim re-checks the lot under its own projection+lot lock and is idempotent on
                // external_ref = "lot-expiry:{lotId}" — a lot drawn-to-zero between the sweep and the reclaim, or
                // an already-reclaimed lot, returns 0 (a safe no-op).
                var reclaimed = await ledger.ReclaimExpiredLotAsync(lot.TenantId, lot.LotId, now, ct);
                if (reclaimed > 0m)
                {
                    reclaimedCount++;
                    reclaimedTotal += reclaimed;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Skip-and-continue: one tenant's reclaim failure must not abort the sweep for the rest.
                LogReclaimFailed(_logger, lot.TenantId.Value, lot.LotId, ex);
            }
        }

        LogReclaimCycleComplete(_logger, reclaimedCount, reclaimedTotal);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Credit lot-expiry reclaim cycle complete — reclaimed {ReclaimedCount} lots totalling {ReclaimedTotal} credits")]
    private static partial void LogReclaimCycleComplete(ILogger logger, int reclaimedCount, decimal reclaimedTotal);

    [LoggerMessage(Level = LogLevel.Error, Message = "Credit lot-expiry reclaim cycle failed")]
    private static partial void LogReclaimCycleFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to reclaim expired credit lot {LotId} for tenant {TenantId}")]
    private static partial void LogReclaimFailed(ILogger logger, string tenantId, string lotId, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Circuit open for worker.credit-lot-expiry-reclaim — skipping cycle")]
    private static partial void LogCircuitOpen(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private static partial void LogWorkerCrash(ILogger logger, string workerName, string reason, Exception ex);
}
