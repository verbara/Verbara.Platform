using System.Collections.Concurrent;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// In-memory twin of <c>PostgresCreditLedgerStore</c>: the append-only signed AI-credit ledger plus its O(1)
/// balance projection. Semantics are <b>identical</b> to the Postgres store — a primary-key projection
/// lookup for the balance (never a <c>SUM</c>), unconditional idempotent grants (deduplicated on
/// <c>period_key</c> for subscription or <c>external_ref</c> for top-up), and a guarded compare-and-decrement
/// debit that can never drive the balance negative even under concurrency. Each tenant's
/// <c>(balance, version)</c> projection cell is mutated under a per-tenant lock that stands in for the
/// Postgres row-level <c>UPDATE … WHERE balance &gt;= @amount</c> guard. The metered debit
/// (<see cref="PostMeteredDebitAsync"/>) mirrors the Postgres two-step covered-plus-PostPaid split
/// (ADR-0033 addendum, Model C). See ADR-0033 / the credit-ledger-substrate spec delta. Change (b)
/// (credit-ledger-cutover) wires the runtime call-sites onto this store behind default-off flags.
/// </summary>
internal sealed class InMemoryCreditLedgerStore : ICreditLedgerStore
{
    private readonly ConcurrentDictionary<TenantId, TenantLedger> _ledgers = new();

    public Task<decimal> GetBalanceAsync(TenantId tenantId, CancellationToken ct)
    {
        // O(1) projection lookup; absent tenant → 0 (mirrors the missing projection row).
        if (!_ledgers.TryGetValue(tenantId, out var ledger))
            return Task.FromResult(0m);

        lock (ledger.Gate)
        {
            return Task.FromResult(ledger.Balance);
        }
    }

    public Task PostGrantAsync(CreditLedgerEntry grant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grant);

        var ledger = _ledgers.GetOrAdd(grant.TenantId, _ => new TenantLedger());

        lock (ledger.Gate)
        {
            // Idempotency arbiter — mirrors the partial unique indexes + ON CONFLICT DO NOTHING:
            // a subscription grant keys on (period_key, entry_type); a top-up keys on external_ref. A grant
            // carrying neither key never collides and always inserts.
            var idempotencyKey = ResolveGrantIdempotencyKey(grant);
            if (idempotencyKey is not null && !ledger.GrantKeys.Add(idempotencyKey))
            {
                // Duplicate grant — no-op: neither double-inserts the ledger row nor double-credits.
                return Task.CompletedTask;
            }

            ledger.Entries.Add(grant);
            ledger.Balance += grant.Amount;
            ledger.Version++;

            // Mint one lot mirroring the grant (Original == Remaining == Amount), with the next monotonic
            // per-tenant lot_seq under the same Gate. Reached only on the non-duplicate branch, so a deduplicated
            // grant never mints a second lot. Keeps Σ(open non-expired lot.Remaining) == Balance after every grant.
            ledger.Lots.Add(new CreditLot
            {
                LotId = grant.EntryId.Value,
                TenantId = grant.TenantId,
                Source = grant.Source,
                Original = grant.Amount,
                Remaining = grant.Amount,
                ExpiresAt = grant.ExpiresAt,
                GrantedAt = grant.CreatedAt,
                LotSeq = ledger.NextLotSeq++,
            });
        }

        return Task.CompletedTask;
    }

    public Task<CreditDebitResult> TryPostDebitAsync(TenantId tenantId, decimal amount, CreditSource source, string? usageRecordId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var ledger = _ledgers.GetOrAdd(tenantId, _ => new TenantLedger());

        lock (ledger.Gate)
        {
            // Guarded compare-and-decrement: the balance >= amount check + mutation happen atomically under
            // the per-tenant lock, standing in for the Postgres single-statement guarded UPDATE. At most one
            // concurrent debit can satisfy the predicate for the last lot, so the balance never goes negative.
            if (ledger.Balance < amount)
                return Task.FromResult(CreditDebitResult.RejectedInsufficientBalance);

            ledger.Balance -= amount;
            ledger.Version++;

            // Append the negative-amount debit row in the same critical section as the projection update. The
            // row records the lot it drew from (source) — never hard-coded PostPaid.
            AppendDebit(ledger, tenantId, source, -amount, usageRecordId, now);

            return Task.FromResult(CreditDebitResult.Posted(ledger.Balance));
        }
    }

    public Task<MeteredDebitResult> PostMeteredDebitAsync(TenantId tenantId, decimal debit, CreditSource coveredSource, string? usageRecordId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var ledger = _ledgers.GetOrAdd(tenantId, _ => new TenantLedger());

        lock (ledger.Gate)
        {
            // Two-step covered-plus-PostPaid split under the per-tenant lock — the InMemory twin of the
            // Postgres single-transaction FOR UPDATE + guarded decrement + unconditional tail.
            var covered = Math.Min(ledger.Balance, debit);
            var tail = debit - covered;

            if (covered > 0m)
            {
                // Draw covered from the prepaid stock; the projection floors at 0 (covered <= balance).
                ledger.Balance -= covered;
                ledger.Version++;
                AppendDebit(ledger, tenantId, coveredSource, -covered, usageRecordId, now);
            }

            if (tail > 0m)
            {
                // Unconditional billable PostPaid tail — does NOT change the (floored) projection balance.
                AppendDebit(ledger, tenantId, CreditSource.PostPaid, -tail, usageRecordId, now);
            }

            return Task.FromResult(new MeteredDebitResult(ledger.Balance, covered, tail));
        }
    }

    public Task<decimal> GetPostPaidDebitsTotalAsync(TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
    {
        if (!_ledgers.TryGetValue(tenantId, out var ledger))
            return Task.FromResult(0m);

        lock (ledger.Gate)
        {
            // PostPaid debit amounts are negative; negate to surface the positive customer-owed overage.
            var total = ledger.Entries
                .Where(e => e.EntryType == CreditEntryType.Debit
                    && e.Source == CreditSource.PostPaid
                    && e.CreatedAt >= periodStart
                    && e.CreatedAt < periodEnd)
                .Sum(e => -e.Amount);

            return Task.FromResult(total);
        }
    }

    public Task<IReadOnlyList<SourceRemaining>> GetRemainingBySourceAsync(TenantId tenantId, DateTimeOffset now, CancellationToken ct)
    {
        if (!_ledgers.TryGetValue(tenantId, out var ledger))
            return Task.FromResult<IReadOnlyList<SourceRemaining>>([]);

        lock (ledger.Gate)
        {
            // Mirror the Postgres GROUP BY: open (Remaining > 0), non-expired (ExpiresAt is null OR > now) lots,
            // summed per source. PostPaid is never a lot so it never appears; expired Promo lots are excluded. The
            // Σ over the returned rows equals Balance (Σ(open non-expired lot.Remaining) == projection.balance).
            IReadOnlyList<SourceRemaining> result = ledger.Lots
                .Where(l => l.Remaining > 0m && (l.ExpiresAt is null || l.ExpiresAt > now))
                .GroupBy(l => l.Source)
                .Select(g => new SourceRemaining(g.Key, g.Sum(l => l.Remaining)))
                .ToList();

            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<CreditLot>> GetLotsAsync(TenantId tenantId, CancellationToken ct)
    {
        if (!_ledgers.TryGetValue(tenantId, out var ledger))
            return Task.FromResult<IReadOnlyList<CreditLot>>([]);

        lock (ledger.Gate)
        {
            // Test/diagnostic read of all lots (including drawn-to-zero and expired). Snapshot copy under Gate.
            IReadOnlyList<CreditLot> result = ledger.Lots.ToList();
            return Task.FromResult(result);
        }
    }

    public Task PostBackfillConsumptionAsync(TenantId tenantId, decimal consumed, string periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodKey);

        var now = DateTimeOffset.UtcNow;

        var ledger = _ledgers.GetOrAdd(tenantId, _ => new TenantLedger());

        lock (ledger.Gate)
        {
            // A non-positive consumption seeds nothing.
            if (consumed <= 0m)
                return Task.CompletedTask;

            // Idempotency arbiter — the InMemory twin of the migration-012 partial unique index on
            // (tenant_id, external_ref): a covered marker row carrying external_ref = "backfill:{periodKey}".
            // If it already exists the whole operation is a complete no-op (mirrors ON CONFLICT DO NOTHING +
            // the 0-row short-circuit in Postgres).
            var externalRef = $"backfill:{periodKey}";
            if (ledger.Entries.Any(e => e.ExternalRef == externalRef))
                return Task.CompletedTask;

            var covered = Math.Min(ledger.Balance, consumed);
            var tail = consumed - covered;

            // Covered (Subscription) marker debit row carrying the external_ref idempotency marker.
            ledger.Entries.Add(new CreditLedgerEntry
            {
                EntryId = EntityId.New(),
                TenantId = tenantId,
                EntryType = CreditEntryType.Debit,
                Source = CreditSource.Subscription,
                Amount = -covered,
                PeriodKey = null,
                ExternalRef = externalRef,
                ExpiresAt = null,
                UsageRecordId = null,
                CreatedAt = now,
            });

            if (covered > 0m)
            {
                // Draw covered from the prepaid stock; the projection floors at 0 (covered <= balance).
                ledger.Balance -= covered;
                ledger.Version++;
            }

            if (tail > 0m)
            {
                // Unconditional billable PostPaid tail — does NOT change the (floored) projection balance.
                AppendDebit(ledger, tenantId, CreditSource.PostPaid, -tail, usageRecordId: null, now);
            }

            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<CreditLedgerEntry>> GetEntriesAsync(TenantId tenantId, int page, int pageSize, CancellationToken ct)
    {
        if (!_ledgers.TryGetValue(tenantId, out var ledger))
            return Task.FromResult<IReadOnlyList<CreditLedgerEntry>>([]);

        lock (ledger.Gate)
        {
            // Most recent first, with a deterministic tiebreak on EntryId so same-instant rows order
            // identically to the Postgres ORDER BY created_at DESC, entry_id DESC. EntityId.Value is an
            // ordinal hex string — Ordinal compare matches Postgres TEXT (C-locale) byte ordering.
            IReadOnlyList<CreditLedgerEntry> result = ledger.Entries
                .OrderByDescending(e => e.CreatedAt)
                .ThenByDescending(e => e.EntryId.Value, StringComparer.Ordinal)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return Task.FromResult(result);
        }
    }

    public Task<int> GetEntriesCountAsync(TenantId tenantId, CancellationToken ct)
    {
        // Total ledger entry count — backs PagedResult.TotalCount. Absent tenant → 0 (no ledger).
        if (!_ledgers.TryGetValue(tenantId, out var ledger))
            return Task.FromResult(0);

        lock (ledger.Gate)
        {
            return Task.FromResult(ledger.Entries.Count);
        }
    }

    private static void AppendDebit(
        TenantLedger ledger,
        TenantId tenantId,
        CreditSource source,
        decimal signedAmount,
        string? usageRecordId,
        DateTimeOffset now)
    {
        ledger.Entries.Add(new CreditLedgerEntry
        {
            EntryId = EntityId.New(),
            TenantId = tenantId,
            EntryType = CreditEntryType.Debit,
            Source = source,
            Amount = signedAmount,
            PeriodKey = null,
            ExternalRef = null,
            ExpiresAt = null,
            UsageRecordId = usageRecordId,
            CreatedAt = now,
        });
    }

    private static string? ResolveGrantIdempotencyKey(CreditLedgerEntry grant)
    {
        // Subscription grants dedupe on (period_key, entry_type); top-ups dedupe on external_ref. The keys
        // are namespaced so a period_key and an external_ref of the same string value never alias.
        if (grant.PeriodKey is { } periodKey)
            return $"period:{(int)grant.EntryType}:{periodKey}";
        if (grant.ExternalRef is { } externalRef)
            return $"extref:{externalRef}";
        return null;
    }

    private sealed class TenantLedger
    {
        public object Gate { get; } = new();
        public List<CreditLedgerEntry> Entries { get; } = [];
        public HashSet<string> GrantKeys { get; } = [];
        public List<CreditLot> Lots { get; } = [];
        public decimal Balance { get; set; }
        public long Version { get; set; }

        // The per-tenant monotonic lot_seq source — the InMemory twin of the credit_lot_seq table. Starts at 0
        // (a brand-new tenant's first lot uses 0); post-incremented under Gate as each grant mints a lot.
        public long NextLotSeq { get; set; }
    }
}
