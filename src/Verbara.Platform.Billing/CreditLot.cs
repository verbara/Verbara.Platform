using Verbara.Platform.Core;

namespace Verbara.Platform.Billing;

/// <summary>
/// A mutable per-grant lot projection (<c>credit_lot</c>) — one row per AI-credit grant. Its
/// <see cref="Remaining"/> balance is guarded-decremented as debits draw from it (FIFO), <see cref="ExpiresAt"/>
/// is finally consulted (expired lots are skipped and later reclaimed), and <see cref="LotSeq"/> is the
/// deterministic monotonic per-tenant tiebreak in the total FIFO/lock order
/// (<c>billable_priority ASC, expires_at ASC NULLS LAST, granted_at ASC, lot_seq ASC</c>). The load-bearing
/// invariant: <c>Σ(open non-expired lot.Remaining) == tenant_credit_balance.balance</c>. See ADR-0033 / the
/// 2026-06-28 (c2) resolution addendum.
/// </summary>
/// <remarks>
/// Pure domain class — no Npgsql reference (the open-core boundary). The <c>static Map(NpgsqlDataReader)</c>
/// reader lives only in the Postgres store, never on this entity.
/// </remarks>
public sealed class CreditLot
{
    /// <summary>Lot identifier — equals the originating grant's <c>entry_id</c> (EntityId hex, persisted as TEXT).</summary>
    public required string LotId { get; init; }

    /// <summary>Owning tenant.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The economic source of the grant that minted this lot. Persisted as a <c>SMALLINT</c> ordinal.</summary>
    public required CreditSource Source { get; init; }

    /// <summary>The lot's granted amount (immutable). Persisted as <c>NUMERIC(18,6)</c>.</summary>
    public required decimal Original { get; init; }

    /// <summary>The undrawn balance remaining in this lot (guarded-decremented, never negative). Persisted as <c>NUMERIC(18,6)</c>.</summary>
    public required decimal Remaining { get; init; }

    /// <summary>When this lot expires, if any. Null = never expires (e.g. subscription/top-up). Expired lots are FIFO-skipped and reclaimed.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When this lot was granted (UTC) — the <c>granted_at</c> FIFO ordering key.</summary>
    public required DateTimeOffset GrantedAt { get; init; }

    /// <summary>The monotonic per-tenant lot sequence — the deterministic final FIFO/lock tiebreak (NOT the random EntityId).</summary>
    public required long LotSeq { get; init; }
}
