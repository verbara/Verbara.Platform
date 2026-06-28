using Verbara.Platform.Core;

namespace Verbara.Platform.Billing;

/// <summary>
/// Persistence contract for the append-only AI-credit ledger (<c>ai_credit_ledger</c>) and its O(1) balance
/// projection (<c>tenant_credit_balance</c>). The balance read is always a primary-key lookup of the
/// projection — never a <c>SUM</c> over the ledger. Grants apply unconditionally and are idempotent on
/// <c>period_key</c> (subscription) or <c>external_ref</c> (top-up); debits are applied by a single guarded
/// projection <c>UPDATE</c> in the same transaction as the ledger row. See ADR-0033 / the
/// credit-ledger-substrate spec delta.
/// <para>
/// Change (b) (credit-ledger-cutover) wires the runtime call-sites onto this contract behind default-off
/// kill-switches: the AiAnalysis quota check reads <see cref="GetBalanceAsync"/>, the metering funnel posts a
/// <see cref="PostMeteredDebitAsync"/>, and the invoice derives customer-owed overage via
/// <see cref="GetPostPaidDebitsTotalAsync"/>. Until those flags flip the ledger remains inert at runtime.
/// </para>
/// </summary>
public interface ICreditLedgerStore
{
    /// <summary>
    /// Returns the tenant's current credit balance via an O(1) primary-key lookup of the
    /// <c>tenant_credit_balance</c> projection. Returns <c>0</c> when no projection row exists.
    /// </summary>
    Task<decimal> GetBalanceAsync(TenantId tenantId, CancellationToken ct);

    /// <summary>
    /// Posts a grant (positive <see cref="CreditLedgerEntry.Amount"/>) atomically: appends the ledger row and,
    /// only if it was actually inserted, increments the projection balance <b>and mints one <c>credit_lot</c>
    /// row mirroring the grant</b> (<see cref="CreditLot.Original"/> = <see cref="CreditLot.Remaining"/> =
    /// <see cref="CreditLedgerEntry.Amount"/>, <see cref="CreditLot.Source"/>, <see cref="CreditLot.ExpiresAt"/>,
    /// <see cref="CreditLot.GrantedAt"/> = <see cref="CreditLedgerEntry.CreatedAt"/>, and a monotonic per-tenant
    /// <see cref="CreditLot.LotSeq"/>) in the same transaction. Idempotent — a grant carrying a
    /// <see cref="CreditLedgerEntry.PeriodKey"/> (subscription) or <see cref="CreditLedgerEntry.ExternalRef"/>
    /// (top-up) that collides with an existing entry is a no-op that neither double-inserts, double-credits, nor
    /// mints a second lot. The minted lot preserves the invariant
    /// <c>Σ(open non-expired lot.remaining) == tenant_credit_balance.balance</c> after every grant.
    /// </summary>
    Task PostGrantAsync(CreditLedgerEntry grant, CancellationToken ct);

    /// <summary>
    /// Attempts to debit <paramref name="amount"/> credits atomically. Applies a single guarded projection
    /// <c>UPDATE … WHERE balance &gt;= @amount</c>; on success (one row affected) appends the negative-amount
    /// ledger debit row in the same transaction and returns <see cref="CreditDebitResult.Posted"/> with the new
    /// balance. On failure (zero rows affected) the transaction is rolled back, no ledger row is written, the
    /// balance is unchanged, and <see cref="CreditDebitResult.RejectedInsufficientBalance"/> is returned. The
    /// balance can never go negative, even under concurrent debits. The debit row records
    /// <paramref name="source"/> — the lot it drew from (e.g. <see cref="CreditSource.Subscription"/> for a
    /// covered prepaid draw) — and MUST NOT hard-code <see cref="CreditSource.PostPaid"/>; mis-tagging a covered
    /// draw as <c>PostPaid</c> would over-bill 100% of consumption (ADR-0033 addendum). <paramref name="usageRecordId"/>
    /// is an optional back-reference to the originating usage record.
    /// </summary>
    Task<CreditDebitResult> TryPostDebitAsync(TenantId tenantId, decimal amount, CreditSource source, string? usageRecordId, CancellationToken ct);

    /// <summary>
    /// Posts a metered consumption debit as a <b>FIFO multi-source allocation</b> in a <b>single transaction</b>
    /// (ADR-0033 / the 2026-06-28 (c2) resolution addendum). The debit is drawn across the tenant's open,
    /// non-expired <c>credit_lot</c> rows in the provably-total order
    /// <c>billable_priority ASC, expires_at ASC NULLS LAST, granted_at ASC, lot_seq ASC</c> (the
    /// <c>tenant_credit_balance</c> row is locked first, then the lots in that same order — the deadlock-avoidance
    /// contract). Each drawn lot emits one source-tagged covered debit row (the lot's own
    /// <see cref="CreditSource"/>) plus an internal <c>credit_allocation</c> row, and the projection is decremented
    /// once by the total covered amount via the guarded <c>UPDATE … WHERE balance &gt;= @covered</c> (the
    /// projection floors at 0 — no lot is ever overdrawn, even under concurrency). The uncovered remainder
    /// <c>tail = debit − Σ covered</c> is posted as exactly <b>one</b> <see cref="CreditSource.PostPaid"/> debit
    /// row — never a lot, never split per lot — that does <b>not</b> touch the projection: the billable overage
    /// that lets a <c>Warn</c> tenant keep serving past a depleted balance. At <c>n = 1</c> (a single
    /// <see cref="CreditSource.Subscription"/> lot) the path degenerates byte-for-byte to the prior two-step
    /// covered-plus-PostPaid split. <see cref="MeteredDebitResult.CoveredAmount"/> is the sum of the per-lot draws.
    /// <paramref name="usageRecordId"/> is an optional back-reference to the originating usage record.
    /// </summary>
    Task<MeteredDebitResult> PostMeteredDebitAsync(TenantId tenantId, decimal debit, string? usageRecordId, CancellationToken ct);

    /// <summary>
    /// Returns the customer-owed AiAnalysis overage for a period: the sum of <c>|amount|</c> over the tenant's
    /// <see cref="CreditSource.PostPaid"/> debit rows whose <c>created_at</c> falls in
    /// <c>[<paramref name="periodStart"/>, <paramref name="periodEnd"/>)</c>. This is the invoice-read source
    /// under the cutover (ADR-0033 addendum) — equal to <c>max(0, consumed − allowance)</c> for an allowance-only
    /// tenant. Returns <c>0</c> when no PostPaid debits exist in the window.
    /// </summary>
    Task<decimal> GetPostPaidDebitsTotalAsync(TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct);

    /// <summary>Returns the tenant's ledger entries, most recent first, paginated (1-based <paramref name="page"/>).</summary>
    Task<IReadOnlyList<CreditLedgerEntry>> GetEntriesAsync(TenantId tenantId, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Returns the total count of the tenant's ledger entries — the <c>TotalCount</c> backing a
    /// <c>PagedResult</c> over <see cref="GetEntriesAsync"/>. Returns <c>0</c> when the tenant has no ledger.
    /// </summary>
    Task<int> GetEntriesCountAsync(TenantId tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the tenant's open (drawable) remaining credit summed per <see cref="CreditSource"/> from the
    /// <c>credit_lot</c> projection, as of <paramref name="now"/>. <b>Excludes</b> any lot with
    /// <c>remaining = 0</c> and any expired lot (a lot whose <c>expires_at</c> is non-null and
    /// <c>&lt;= <paramref name="now"/></c> — i.e. only <c>expires_at IS NULL OR expires_at &gt; now</c> lots count),
    /// which in practice removes lapsed Promo lots pending reclaim. <see cref="CreditSource.PostPaid"/> is never a
    /// lot (the uncovered billable tail is posted directly as a debit row with no lot) so it never appears.
    /// <para>
    /// <b>Invariant:</b> the sum of <see cref="SourceRemaining.Remaining"/> across the returned sources equals
    /// <see cref="GetBalanceAsync"/> for the same tenant — the lot projection re-derives the O(1) balance
    /// projection (<c>Σ(open non-expired lot.remaining) == tenant_credit_balance.balance</c>, ADR-0033 / the
    /// 2026-06-28 (c2) resolution addendum). Sources with no open remaining are omitted from the result.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<SourceRemaining>> GetRemainingBySourceAsync(TenantId tenantId, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Returns all <see cref="CreditLot"/> rows for the tenant (including drawn-to-zero and expired lots) — a
    /// test/diagnostic read of the raw <c>credit_lot</c> projection. Ordering is unspecified but stable. Use
    /// <see cref="GetRemainingBySourceAsync"/> for the drawable per-source remaining (which applies the open /
    /// non-expired filters).
    /// </summary>
    Task<IReadOnlyList<CreditLot>> GetLotsAsync(TenantId tenantId, CancellationToken ct);

    /// <summary>
    /// Idempotently seeds one period's already-realised AI-credit consumption onto the ledger for the cutover
    /// back-fill (ADR-0033 addendum, task B7). Splits <paramref name="consumed"/> exactly as the runtime metered
    /// debit would have: <c>covered = min(balance, consumed)</c> is drawn from the (already-minted) Subscription
    /// grant and recorded as a <see cref="CreditSource.Subscription"/> debit row, and the uncovered
    /// <c>tail = consumed − covered</c> is recorded as an unconditional <see cref="CreditSource.PostPaid"/> debit
    /// (the customer-owed overage) that does <b>not</b> touch the projection. The <b>whole</b> operation is
    /// idempotent on a single covered-marker row carrying <c>external_ref = "backfill:{periodKey}"</c> (the
    /// migration-012 partial unique index on <c>(tenant_id, external_ref)</c> is the arbiter): a re-run finds the
    /// marker already present and is a complete no-op (no second debit, no second balance decrement, no second
    /// PostPaid tail). A non-positive <paramref name="consumed"/> is a no-op. This seeds at most one Subscription
    /// debit and one PostPaid debit per <paramref name="periodKey"/>.
    /// </summary>
    Task PostBackfillConsumptionAsync(TenantId tenantId, decimal consumed, string periodKey, CancellationToken ct);
}
