namespace Verbara.Platform.Billing;

/// <summary>
/// The outcome of a two-step metered debit against the AI-credit ledger
/// (<see cref="ICreditLedgerStore.PostMeteredDebitAsync"/>). In one transaction the debit is split into a
/// <see cref="CoveredAmount"/> drawn from the prepaid stock (the guarded projection <c>UPDATE</c>, so the
/// projection floors at 0 and the prepaid lot is never overdrawn) and an uncovered
/// <see cref="PostPaidAmount"/> tail posted as an unconditional <c>PostPaid</c> debit row that does not touch
/// the projection — the billable overage (ADR-0033 addendum, Model C). <see cref="NewBalance"/> is the
/// post-debit projection balance (always <c>&gt;= 0</c>). A reflection-free, allocation-free
/// <see langword="readonly"/> <see langword="record"/> <see langword="struct"/> (AOT-safe).
/// </summary>
/// <param name="NewBalance">The post-debit projection balance (floored at 0).</param>
/// <param name="CoveredAmount">The portion of the debit drawn from the prepaid stock (recorded against the covered lot).</param>
/// <param name="PostPaidAmount">The uncovered remainder posted as a billable <c>PostPaid</c> debit tail (0 when fully covered).</param>
public readonly record struct MeteredDebitResult(decimal NewBalance, decimal CoveredAmount, decimal PostPaidAmount);
