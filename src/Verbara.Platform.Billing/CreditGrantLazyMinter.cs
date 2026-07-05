using Verbara.Platform.Core;

namespace Verbara.Platform.Billing;

/// <summary>
/// credit-grant-lazy-mint-rollover (Platform/ADR-0033 addendum, 2026-07-04): closes the known month-rollover
/// window recorded on <see cref="CreditGrantMintWorker"/> — a tenant that first consumes after a UTC month
/// boundary but before the worker's next tick (≤ one <see cref="DunningConfig.CheckIntervalHours"/> interval)
/// would otherwise see no current-period <see cref="CreditSource.Subscription"/> grant, so its balance read
/// returns the prior carry-over only. Called inline on the enforcement (<c>DefaultQuotaEnforcementService</c>)
/// and readout (<c>CreditLedgerEndpoints</c>) balance-read paths, <b>before</b> the balance is read.
/// <para>
/// Steady-state cost: exactly one indexed existence check (<see cref="ICreditLedgerStore.HasCurrentPeriodGrantAsync"/>,
/// the <c>uq_ai_credit_ledger_period</c> partial unique index) per call — a read, never a write, once the
/// scheduled worker has minted the period's grant. The mint only fires on a miss, and reuses the exact posting
/// path (<see cref="ICreditLedgerStore.PostGrantAsync"/>) the worker uses, so worker/lazy races are safe
/// no-ops on either side (idempotent on <c>(tenant_id, period_key, entry_type)</c> <c>ON CONFLICT DO NOTHING</c>
/// + conditional projection/lot upsert).
/// </para>
/// </summary>
public sealed class CreditGrantLazyMinter
{
    private readonly ICreditLedgerStore _ledger;
    private readonly IClock _clock;

    public CreditGrantLazyMinter(ICreditLedgerStore ledger, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(clock);
        _ledger = ledger;
        _clock = clock;
    }

    /// <summary>
    /// Mints the tenant's current-period <see cref="CreditSource.Subscription"/> grant inline when
    /// <paramref name="quota"/>'s <see cref="TenantQuota.AiCreditsMonthly"/> is non-null and no current-period
    /// grant exists yet. A null <paramref name="quota"/> or a null <see cref="TenantQuota.AiCreditsMonthly"/> is
    /// a no-op (unlimited / pay-as-you-go tenants never carry a subscription grant). <b>Must</b> be called
    /// before the balance is read on that same request so a first-of-period read observes the mint.
    /// </summary>
    public async Task EnsureCurrentPeriodGrantAsync(TenantQuota? quota, CancellationToken ct)
    {
        if (quota?.AiCreditsMonthly is not { } allowance)
            return;

        var period = BillingPeriod.Current(_clock);
        var now = _clock.UtcNow;

        // Indexed existence check FIRST — the steady-state no-write guarantee. Only a miss (the rollover
        // window) falls through to the mint.
        if (await _ledger.HasCurrentPeriodGrantAsync(quota.TenantId, period.Key, ct))
            return;

        // Mirrors CreditGrantMintWorker.ProcessMintCycleAsync's posting exactly, so worker/lazy races are safe:
        // idempotent on (tenant_id, period_key, entry_type).
        await _ledger.PostGrantAsync(
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
}
