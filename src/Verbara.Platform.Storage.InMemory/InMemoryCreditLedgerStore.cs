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

    public Task<IReadOnlyList<CreditLedgerEntry>> GetEntriesAsync(TenantId tenantId, int page, int pageSize, CancellationToken ct)
    {
        if (!_ledgers.TryGetValue(tenantId, out var ledger))
            return Task.FromResult<IReadOnlyList<CreditLedgerEntry>>([]);

        lock (ledger.Gate)
        {
            // Most recent first. Append order is insertion order; reverse to mirror the Postgres
            // ORDER BY created_at DESC, entry_id DESC for entries appended within the same instant.
            IReadOnlyList<CreditLedgerEntry> result = ledger.Entries
                .AsEnumerable()
                .Reverse()
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return Task.FromResult(result);
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
        public decimal Balance { get; set; }
        public long Version { get; set; }
    }
}
