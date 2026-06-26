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
/// This substrate is inert as of the credit-ledger-substrate change — nothing reads or writes the ledger at
/// runtime yet; the store and projection exist and are unit-tested in isolation.
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
    /// balance can never go negative, even under concurrent debits. <paramref name="usageRecordId"/> is an
    /// optional back-reference to the originating usage record.
    /// </summary>
    Task<CreditDebitResult> TryPostDebitAsync(TenantId tenantId, decimal amount, string? usageRecordId, CancellationToken ct);

    /// <summary>Returns the tenant's ledger entries, most recent first, paginated (1-based <paramref name="page"/>).</summary>
    Task<IReadOnlyList<CreditLedgerEntry>> GetEntriesAsync(TenantId tenantId, int page, int pageSize, CancellationToken ct);
}
