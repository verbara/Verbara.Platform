---
tier: GRANDE
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

Change **(c2)** of the AI-credit-ledger program (ADR-0033 + its 2026-06-27 (c) addendum) — the higher-risk
half that c1 deliberately deferred. With `TopUp` fungible credits shipped (c1), this change adds the
**lot-allocation machinery** and the two remaining grant sources that need it: **`Promo`** (expiring) and
**`Partner`** (attributable). The PO chose the **full allocation substrate** (not a lean hybrid) because
per-source remaining reporting is on the near-term roadmap — pay the cost once now, not as a later hot-migration.

> Full spec + tasks authored at the start of this change (after c1 merges), re-grounded. Outline per ADR-0033
> (c) addendum.

## What Changes

- **`credit_lot` projection** — one mutable row per grant (`remaining` guarded-decremented, `expires_at`
  finally consulted, `granted_at`), so per-source remaining is reportable and `Promo` can expire.
- **`credit_allocation` table** — one row per (debit × lot) recording the FIFO draw (debit→lot linkage).
- **FIFO metered debit** — `PostMeteredDebitAsync` walks open, non-expired lots in the draw order
  **`Promo (soonest-expiring) → Partner → Subscription/TopUp → PostPaid`** (static `billable_priority` map;
  the locking `SELECT … FOR UPDATE` ORDER BY equals this — the deadlock contract), emitting one source-tagged
  debit row per layer; invoice stays `Σ |PostPaid|`. The (a)/(b) characterization tests stay green at n=1.
- **`Promo` grants + expiry** — `Promo` grant source (operator-minted, `expires_at`); expired lots are
  skipped in FIFO + a reclaim sweeper posts an offsetting append-only `Promo` debit so expired remaining
  actually leaves the balance.
- **`Partner` grants + attribution** — `Partner` grant source; partner draws derive-on-read (`Σ |Partner
  debits|`) **and** a period-close step writes a **non-invoice-keyed `partner_credit_allocation`** record
  `(partner_tenant_id, customer_tenant_id, period_key, credits)` (because a partner-funded tenant may have no
  customer invoice — `PartnerRevenueRecord` is invoice-keyed and the wrong home). Resolve the owning partner via
  `Tenant.ParentTenantId` **gated on `parent.Type == TenantType.Partner`** (a check that does not exist today).
- **Reporting + endpoints** — per-source remaining readout (`GetRemainingBySourceAsync`); operator `Promo`/
  `Partner` grant endpoints (reuse `billing:credits:grant`).

## Capabilities

### Modified Capabilities

- `ai-credit-ledger`: adds per-grant lots, debit→lot FIFO allocation, `Promo` expiry, `Partner` attribution to
  a non-invoice-keyed store, and per-source remaining reporting. Delta authored at change start.

## Impact

- `Verbara.Platform.Billing` (`ICreditLedgerStore` FIFO rewrite + new reads + `IPartnerCreditAllocationStore`),
  `Storage.Postgres`/`Storage.InMemory` (migration: `credit_lot` + `credit_allocation` +
  `partner_credit_allocation` + FIFO/lock indexes; twins), `Verbara.Platform.Api` (grant endpoints + reporting
  DTOs), period-close worker for partner attribution + promo-expiry sweeper. Authoritative: ADR-0033 + (c)
  addendum. Web per-source widget = separate Platform.Web PR.
