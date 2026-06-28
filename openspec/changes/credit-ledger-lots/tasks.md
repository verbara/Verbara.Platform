# Tasks — credit-ledger-lots (c2)

> Authored in full at the start of this change (after `credit-ledger-topups` merges), re-grounded.
> Outline per ADR-0033 (c) addendum 2026-06-27 (full allocation substrate chosen).

## 1. Lot substrate
- [ ] 1.1 Migration: `credit_lot` (lot_id=grant entry_id, source, original, remaining CHECK≥0, expires_at,
  granted_at, version) + `credit_allocation` (debit_entry_id, lot_id, source, amount) + `partner_credit_allocation`
  (partner, customer, period_key PK) + FIFO/lock + partner-debit indexes.
- [ ] 1.2 `PostGrantAsync` also inserts a `credit_lot` row (same tx, only when the grant inserted).

## 2. FIFO metered debit
- [ ] 2.1 Rewrite `PostMeteredDebitAsync` to FIFO-walk open non-expired lots (static `billable_priority` map
  Promo→Partner→Subscription/TopUp; locking SELECT…FOR UPDATE ORDER BY = consumption order), per-lot guarded
  decrement + `credit_allocation` row, PostPaid tail unchanged. `MeteredDebitResult` shape unchanged.
- [ ] 2.2 InMemory twin mirrors the same deterministic order; (b) characterization tests stay green at n=1.

## 3. Promo + Partner
- [ ] 3.1 `Promo` grant source + expiry reclaim sweeper (offsetting append-only debit; idempotent on
  `external_ref="promo-expiry:{lotId}"`).
- [ ] 3.2 `Partner` grant source + partner resolution (`ParentTenantId` gated `Type==Partner`) + period-close
  write to `partner_credit_allocation` + derive-on-read `Σ|Partner debits|`. New `IPartnerCreditAllocationStore`.

## 4. Reporting + API
- [ ] 4.1 `GetRemainingBySourceAsync` (per-source open remaining, expiry-filtered). Operator Promo/Partner grant
  endpoints (reuse `billing:credits:grant`). DTOs in `ApiJsonContext`.

## 5. Verification
- [ ] 5.1 Build 0 warnings; full test green; AOT gate; CI green. Web per-source widget = separate Platform.Web PR.
