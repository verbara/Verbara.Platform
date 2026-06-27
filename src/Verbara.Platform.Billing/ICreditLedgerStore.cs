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
    /// only if it was actually inserted, increments the projection balance in the same transaction. Idempotent
    /// — a grant carrying a <see cref="CreditLedgerEntry.PeriodKey"/> (subscription) or
    /// <see cref="CreditLedgerEntry.ExternalRef"/> (top-up) that collides with an existing entry is a no-op
    /// that neither double-inserts nor double-credits.
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
    /// Posts a metered consumption debit as a two-step covered-plus-PostPaid split in a <b>single
    /// transaction</b> (ADR-0033 addendum, Model C). The covered portion <c>covered = min(balance, debit)</c> is
    /// drawn from the prepaid stock via the guarded projection <c>UPDATE … WHERE balance &gt;= @covered</c> (the
    /// projection floors at 0 — the prepaid lot is never overdrawn) and recorded as a debit row tagged
    /// <paramref name="coveredSource"/>. The uncovered remainder <c>tail = debit − covered</c> is posted as an
    /// <b>unconditional</b> debit row tagged <see cref="CreditSource.PostPaid"/> that does <b>not</b> touch the
    /// projection — the billable overage that lets a <c>Warn</c> tenant keep serving past a depleted balance.
    /// Returns the post-debit balance and the covered / post-paid split. <paramref name="usageRecordId"/> is an
    /// optional back-reference to the originating usage record.
    /// </summary>
    Task<MeteredDebitResult> PostMeteredDebitAsync(TenantId tenantId, decimal debit, CreditSource coveredSource, string? usageRecordId, CancellationToken ct);

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
}
