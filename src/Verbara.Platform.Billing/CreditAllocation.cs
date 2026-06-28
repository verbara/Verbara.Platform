namespace Verbara.Platform.Billing;

/// <summary>
/// An internal debit→lot linkage row (<c>credit_allocation</c>) — one per (debit × drawn lot). Records a single
/// FIFO draw: how much (<see cref="Amount"/>) a metered debit (<see cref="DebitEntryId"/>) drew from a given lot
/// (<see cref="LotId"/>), tagged with that lot's <see cref="Source"/>. This table is <b>internal</b> — it is
/// invisible to <c>GetEntriesAsync</c>/<c>GetEntriesCountAsync</c> (which stay scoped to <c>ai_credit_ledger</c>),
/// so the balance/entries API is unchanged. See ADR-0033 / the 2026-06-28 (c2) resolution addendum.
/// </summary>
/// <remarks>
/// Pure domain class — no Npgsql reference (the open-core boundary). The <c>static Map(NpgsqlDataReader)</c>
/// reader lives only in the Postgres store, never on this entity.
/// </remarks>
public sealed class CreditAllocation
{
    /// <summary>Unique allocation identifier (EntityId hex, persisted as TEXT).</summary>
    public required string AllocationId { get; init; }

    /// <summary>The covered debit ledger entry this allocation belongs to (the <c>ai_credit_ledger</c> debit row's <c>entry_id</c>).</summary>
    public required string DebitEntryId { get; init; }

    /// <summary>The lot this draw decremented (<see cref="CreditLot.LotId"/>).</summary>
    public required string LotId { get; init; }

    /// <summary>The drawn lot's source (mirrors the covered debit row's source). Persisted as a <c>SMALLINT</c> ordinal.</summary>
    public required CreditSource Source { get; init; }

    /// <summary>The amount drawn from the lot by this debit (positive). Persisted as <c>NUMERIC(18,6)</c>.</summary>
    public required decimal Amount { get; init; }

    /// <summary>When this allocation was recorded (UTC).</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
