namespace Verbara.Platform.Billing;

/// <summary>
/// The open (drawable) remaining credit for a single <see cref="CreditSource"/>, used by per-source remaining
/// reporting (<c>GetRemainingBySourceAsync</c>). Excludes expired Promo lots and the synthetic <c>PostPaid</c>
/// source (which has no lot); <c>Σ Remaining over the reported sources == tenant_credit_balance.balance</c>. A
/// reflection-free, allocation-free <see langword="readonly"/> <see langword="record"/>
/// <see langword="struct"/> (AOT-safe). See ADR-0033 / the 2026-06-28 (c2) resolution addendum.
/// </summary>
/// <param name="Source">The economic source the remaining credit belongs to.</param>
/// <param name="Remaining">The open, non-expired remaining credit for that source.</param>
public readonly record struct SourceRemaining(CreditSource Source, decimal Remaining);
