---
tier: GRANDE
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

Change **(c2)** of the AI-credit-ledger program (ADR-0033 + its 2026-06-27 (c) addendum + the **2026-06-28 (c2)
resolution addendum**) — the higher-risk half c1 deliberately deferred, the **money-path rewrite**. With `TopUp`
fungible credits shipped (c1), c2 adds the **lot-allocation machinery** and the two remaining grant sources that need
it: **`Promo`** (expiring) and **`Partner`** (attributable). The PO chose the **full allocation substrate** (mutable
`credit_lot` + `credit_allocation`) because per-source remaining reporting is on the near-term roadmap — pay once now,
not a later hot-migration.

> This proposal was **re-grounded against the real post-c1 code** by an 8-reader workflow + an adversarial critic, then
> a 3-judge panel + a false-trichotomy critic on the one load-bearing decision. That grounding **corrected three
> must-fixes** the 2026-06-27 addendum had asserted from memory — recorded in the **ADR-0033 (c2) resolution addendum**,
> authoritative where it disagrees with the (c) addendum.

## What Changes

- **`credit_lot` projection** — one mutable row per grant: `lot_id` (= grant `entry_id`), `tenant_id`, `source`,
  `original`, `remaining` (`CHECK remaining >= 0`, guarded-decremented), `expires_at`, `granted_at`, and a **monotonic
  per-tenant `lot_seq BIGINT`** (the deterministic FIFO tiebreak — NOT the random `EntityId`). `PostGrantAsync` inserts
  the lot in the **same tx** as the grant, **only when the grant row actually inserted** (`if (inserted == 1)`).
- **`credit_allocation` table** — one **internal** row per (debit × lot): `debit_entry_id`, `lot_id`, `source`,
  `amount`. Records the FIFO draw (debit→lot linkage). Invisible to `GetEntriesAsync`/`GetEntriesCountAsync` (those stay
  scoped to `ai_credit_ledger`, so c1's balance/entries API is unchanged).
- **FIFO metered debit** — `PostMeteredDebitAsync` walks open, non-expired lots in the **provably total** order
  **`billable_priority ASC, expires_at ASC NULLS LAST, granted_at ASC, lot_seq ASC`** (`billable_priority` a static
  draw-priority map `Promo→Partner→Subscription/TopUp`, distinct from the `CreditSource` ordinal). Each drawn lot emits
  one **source-tagged** covered debit row + a `credit_allocation` row; the uncovered remainder is **exactly one**
  `PostPaid` tail (not a lot, never split). **Lock order is fixed: the `tenant_credit_balance` row FIRST** (existing
  `FOR UPDATE`), then lots in that total order — the deadlock-avoidance contract, obeyed by every writer. `MeteredDebitResult`
  shape is unchanged (`CoveredAmount = Σ` per-lot draws). At **n=1** (single Subscription lot) the path degenerates
  byte-for-byte to the (b) two-step; the (a)/(b) characterization tests are the regression guard.
- **`Promo` grants + expiry reclaim** — `Promo` grant source (operator-minted, `expires_at`). Expired lots are skipped
  in FIFO (`expires_at IS NULL OR expires_at > now`) **and** an hourly reclaim sweeper (the `CreditGrantMintWorker`
  `BackgroundService` pattern) posts an offsetting `Promo` **debit of `lot.remaining`** (read `FOR UPDATE`) carrying
  `external_ref = "promo-expiry:{lotId}"` `ON CONFLICT DO NOTHING`, and **only if `inserted == 1`** decrements the
  projection by that `remaining` + zeroes the lot — so it can't reclaim consumed credits or run twice.
- **`Partner` grants + DERIVE-ON-READ attribution** — `Partner` grant source (operator-minted), drawn before
  Subscription/TopUp, **never customer-billed** (a `Partner` debit is never `PostPaid`, so the `Σ PostPaid` invoice
  already excludes it). Attribution is **computed on read** — `GetPartnerAttributionAsync(partnerTenantId, periodStart,
  periodEnd)` = `Σ |Partner-source debits|` over the partner's `GetChildrenAsync` customers in the half-open window,
  resolved via the **existing** `Tenant.ParentTenantId` + `parent.Type == TenantType.Partner` single-hop gate (ZERO
  `Tenant`-model change). The materialized `partner_credit_allocation` table is **deferred** to the future
  partner-billing/settlement change that actually reads it (the attribution facts are already persisted at draw time in
  `credit_allocation(source=Partner)` + the debit `tenant_id`, so deriving cannot drift or double-count).
- **Per-source remaining + grant endpoints** — `GetRemainingBySourceAsync` (per-source open, expiry-filtered, excludes
  PostPaid; `Σ == projection.balance`). Operator `Promo`/`Partner` grant endpoints mirror the c1 top-up double-lock
  (`PlatformAdminOnly` group + `CreditGrantGate` = `PlatformAdminRequirement("billing:credits:grant")`). **No new RBAC
  seeding** (the perms ship from c1; partner self-serve is out of c2).
- **Migration 013** seeds one synthetic `Subscription`-priority lot per tenant with `remaining = current balance`
  (`lot_seq = 0`, no expiry) so the invariant `Σ(open non-expired lot.remaining) == projection.balance` holds from day
  one without reconstructing per-grant history.

## Capabilities

### Modified Capabilities

- `ai-credit-ledger`: adds per-grant lots, debit→lot FIFO allocation, `Promo` expiry reclaim, `Partner` derive-on-read
  attribution, and per-source remaining reporting. Delta below.

## Impact

- `Verbara.Platform.Billing` (`ICreditLedgerStore` FIFO rewrite + `GetRemainingBySourceAsync` +
  `GetPartnerAttributionAsync` + the promo-expiry sweeper `BackgroundService`), `Storage.Postgres`/`Storage.InMemory`
  (migration 013: `credit_lot` + `credit_allocation` + FIFO/lock indexes + the `lot_seq` sequence; both twins),
  `Verbara.Platform.Api` (Promo/Partner grant endpoints + per-source remaining DTOs in `ApiJsonContext`; one
  `AddHostedService` + one resilience-policy registration). **Money-path** (`PostMeteredDebitAsync`) and **AOT** are the
  risk surfaces. Authoritative: ADR-0033 + the (c2) resolution addendum. Web per-source widget = separate Platform.Web
  PR. Deferred fast-follow: the materialized `partner_credit_allocation` table + its trigger.
