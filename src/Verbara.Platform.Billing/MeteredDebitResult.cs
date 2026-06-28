namespace Verbara.Platform.Billing;

/// <summary>
/// The outcome of a FIFO multi-source metered debit against the AI-credit ledger
/// (<see cref="ICreditLedgerStore.PostMeteredDebitAsync"/>). In one transaction the debit is split into a
/// <see cref="CoveredAmount"/> drawn across the tenant's open lots in the draw order Promo→Partner→
/// Subscription/TopUp (each drawn lot decremented + recorded as its own covered debit row, so the projection
/// floors at 0 and no lot is ever overdrawn) and an uncovered <see cref="PostPaidAmount"/> tail posted as a
/// single unconditional <c>PostPaid</c> debit row that does not touch the projection — the billable overage
/// (ADR-0033 / the 2026-06-28 (c2) addendum). <see cref="NewBalance"/> is the post-debit projection balance
/// (always <c>&gt;= 0</c>). A reflection-free, allocation-free <see langword="readonly"/>
/// <see langword="record"/> <see langword="struct"/> (AOT-safe).
/// </summary>
/// <param name="NewBalance">The post-debit projection balance (floored at 0).</param>
/// <param name="CoveredAmount">The Σ of the per-lot FIFO draws across the tenant's open lots, in the draw order Promo→Partner→Subscription/TopUp.</param>
/// <param name="PostPaidAmount">The single uncovered tail posted as a billable <c>PostPaid</c> debit row (0 when fully covered).</param>
public readonly record struct MeteredDebitResult(decimal NewBalance, decimal CoveredAmount, decimal PostPaidAmount);
