using Verbara.Platform.Core;

namespace Verbara.Platform.Billing;

/// <summary>
/// An immutable, signed entry in the append-only AI-credit ledger (<c>ai_credit_ledger</c>). Grants carry a
/// positive <see cref="Amount"/>, debits a negative one. Entries are never updated or deleted; corrections
/// are offsetting entries. The running balance is materialized in the O(1) <c>tenant_credit_balance</c>
/// projection — the ledger remains independently re-derivable (<c>SUM(amount) == balance</c>) as an offline
/// audit assertion. See ADR-0033 / the credit-ledger-substrate spec delta.
/// </summary>
public sealed class CreditLedgerEntry : ITenantScoped
{
    /// <summary>Unique ledger entry identifier (EntityId hex, persisted as TEXT).</summary>
    public required EntityId EntryId { get; init; }

    /// <summary>Owning tenant.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>Whether this entry adds (<see cref="CreditEntryType.Grant"/>) or removes (<see cref="CreditEntryType.Debit"/>) credits.</summary>
    public required CreditEntryType EntryType { get; init; }

    /// <summary>The economic source of the entry (subscription allowance, top-up, promo, partner allocation, or synthetic post-paid lot).</summary>
    public required CreditSource Source { get; init; }

    /// <summary>Signed credit amount: positive for grants, negative for debits. Persisted as <c>NUMERIC(18,6)</c>.</summary>
    public required decimal Amount { get; init; }

    /// <summary>Subscription-grant idempotency key — the <c>"yyyy-MM"</c> UTC period (null for non-subscription entries).</summary>
    public string? PeriodKey { get; init; }

    /// <summary>Top-up idempotency key (null for non-top-up entries).</summary>
    public string? ExternalRef { get; init; }

    /// <summary>When this (grant) lot expires, if any. Null = never expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Back-reference to the originating usage record for debit entries (null for grants).</summary>
    public string? UsageRecordId { get; init; }

    /// <summary>When this entry was appended (UTC).</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Whether a <see cref="CreditLedgerEntry"/> adds or removes credits. Persisted as a <c>SMALLINT</c> ordinal.</summary>
public enum CreditEntryType
{
    /// <summary>Adds credits to the balance (positive amount).</summary>
    Grant = 0,

    /// <summary>Removes credits from the balance (negative amount).</summary>
    Debit = 1,
}

/// <summary>The economic source of a <see cref="CreditLedgerEntry"/>. Persisted as a <c>SMALLINT</c> ordinal.</summary>
public enum CreditSource
{
    /// <summary>Recurring monthly subscription allowance grant.</summary>
    Subscription = 0,

    /// <summary>One-time prepaid top-up grant.</summary>
    TopUp = 1,

    /// <summary>Promotional grant.</summary>
    Promo = 2,

    /// <summary>Partner allocation grant.</summary>
    Partner = 3,

    /// <summary>Synthetic post-paid lot — the uncovered, billable remainder.</summary>
    PostPaid = 4,
}
