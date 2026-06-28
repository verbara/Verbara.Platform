namespace Verbara.Platform.Billing;

/// <summary>
/// The static FIFO draw-priority map over <see cref="CreditSource"/> — the first key of the total
/// FIFO/lock order (<c>billable_priority ASC, expires_at ASC NULLS LAST, granted_at ASC, lot_seq ASC</c>).
/// Lower draws first: <b>Promo → Partner → Subscription/TopUp → PostPaid</b> — burn expiry-risk credits first,
/// then the partner gift (consumed-and-attributed before the customer's own prepaid), then the customer's
/// prepaid, then the uncovered billable tail.
/// <para>
/// <b>This priority is DISTINCT from the persistence <see cref="CreditSource"/> ordinal</b>
/// (<c>Subscription=0, TopUp=1, Promo=2, Partner=3, PostPaid=4</c>). The FIFO walk must order by this map
/// (a SQL <c>CASE</c> / a joined static map in Postgres, an <c>OrderBy</c> in the InMemory twin) — <b>never</b>
/// <c>ORDER BY source</c> — and both stores must agree exactly (the deadlock-avoidance / no-overdraw contract).
/// </para>
/// <para>
/// <see cref="CreditSource.PostPaid"/> is never a real lot (the uncovered tail is posted directly as a debit
/// row with no lot); it sorts last via <see cref="int.MaxValue"/> so it can never precede a drawable lot.
/// </para>
/// Allocation-free and reflection-free (AOT-safe). See ADR-0033 / the 2026-06-28 (c2) resolution addendum.
/// </summary>
public static class BillablePriority
{
    /// <summary>Draw-priority of <see cref="CreditSource.Promo"/> — expiry-risk credits are consumed first.</summary>
    public const int Promo = 0;

    /// <summary>Draw-priority of <see cref="CreditSource.Partner"/> — the partner gift, after promos, before the customer's own prepaid.</summary>
    public const int Partner = 1;

    /// <summary>Draw-priority of the customer's own prepaid stock (<see cref="CreditSource.Subscription"/> and <see cref="CreditSource.TopUp"/> share this rank).</summary>
    public const int Prepaid = 2;

    /// <summary>Draw-priority of <see cref="CreditSource.PostPaid"/> — not a lot; sorts last so it never precedes a drawable lot.</summary>
    public const int PostPaidLast = int.MaxValue;

    /// <summary>
    /// The static draw-priority of a <paramref name="source"/> (lower draws first). Distinct from the
    /// persistence ordinal — see the type summary. <see cref="CreditSource.PostPaid"/> (not a lot) returns
    /// <see cref="int.MaxValue"/>.
    /// </summary>
    public static int Of(CreditSource source) => source switch
    {
        CreditSource.Promo => Promo,
        CreditSource.Partner => Partner,
        CreditSource.Subscription => Prepaid,
        CreditSource.TopUp => Prepaid,
        CreditSource.PostPaid => PostPaidLast,
        _ => PostPaidLast,
    };
}
