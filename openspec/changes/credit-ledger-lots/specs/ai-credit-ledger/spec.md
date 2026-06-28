## ADDED Requirements

### Requirement: Per-grant lots with a provably-total FIFO multi-source allocation order
The system SHALL maintain a mutable `credit_lot` row per grant (`remaining` guarded-decremented, `CHECK remaining >= 0`,
plus `source`, `original`, `expires_at`, `granted_at`, and a monotonic per-tenant `lot_seq`), inserted in the same
transaction as the grant and only when the grant row actually inserted. `PostMeteredDebitAsync` SHALL allocate each
metered debit FIFO across open, non-expired lots in the order `billable_priority ASC, expires_at ASC NULLS LAST,
granted_at ASC, lot_seq ASC` — where `billable_priority` is the static draw-priority map
`Promo → Partner → Subscription/TopUp` (distinct from the `CreditSource` persistence ordinal). This order SHALL be a
total order, identical in the Postgres and InMemory stores, equal to the `SELECT … FOR UPDATE` lock order, with the
`tenant_credit_balance` row locked first; no lot SHALL be overdrawn under concurrency. Each drawn lot SHALL emit one
source-tagged covered debit row plus a `credit_allocation(debit_entry_id, lot_id, source, amount)` row; the uncovered
remainder SHALL be exactly one `PostPaid` tail row (never a lot, never split per lot). `credit_allocation` SHALL be
internal — `GetEntriesAsync`/`GetEntriesCountAsync` and `GetPostPaidDebitsTotalAsync` are unchanged. The invariant
`Σ(open non-expired lot.remaining) == tenant_credit_balance.balance` SHALL hold after every mutation, and the customer
invoice SHALL remain `Σ |PostPaid debits|`.

#### Scenario: Draw spans promo then subscription then PostPaid
- **GIVEN** a tenant with a 100-credit Promo lot and a 1000-credit Subscription lot who consumes 1150 credits
- **THEN** 100 is drawn from Promo, 1000 from Subscription (each a source-tagged covered debit + a `credit_allocation`
  row), and 50 is a single `PostPaid` (billable) tail row; `CoveredAmount == 1100`, `PostPaidAmount == 50`

#### Scenario: Single-Subscription tenant is byte-identical to the two-step (n=1)
- **GIVEN** a tenant with one 10-credit Subscription lot who debits 4
- **THEN** the result is `NewBalance 6, Covered 4, PostPaid 0`, exactly one `-4` `Subscription` ledger debit row and
  zero `PostPaid` rows (the (a)/(b) characterization values), plus one internal `credit_allocation` row invisible to
  `GetEntriesAsync`

#### Scenario: No open lots yields a pure PostPaid tail
- **GIVEN** a tenant with zero open lots who consumes 7 credits
- **THEN** nothing is drawn from any lot and the whole 7 is a single `PostPaid` tail row (`Covered 0, PostPaid 7`),
  identical to the current depleted-tenant behavior

#### Scenario: Concurrent debits never overdraw a lot
- **GIVEN** a tenant with a single 4-credit lot and two concurrent debits of 3
- **THEN** the total covered across both never exceeds 4, no lot `remaining` goes negative, and no deadlock occurs
  (both lock the projection row then the lot in the same total order)

### Requirement: Intra-tier drain is soonest-expiring-first (use-it-or-lose-it)
At equal `billable_priority`, lots SHALL drain by `expires_at ASC NULLS LAST` then `granted_at ASC` then `lot_seq ASC`.
A monthly `Subscription` lot (expires at period end) SHALL therefore drain before a non-expiring `TopUp` lot, preserving
the customer's purchased credits and burning the free expiring allowance first.

#### Scenario: Expiring subscription drains before persistent top-up
- **GIVEN** a tenant with a 1000-credit Subscription lot expiring at period end and a 500-credit non-expiring TopUp lot
- **WHEN** the tenant consumes 1200
- **THEN** 1000 is drawn from the Subscription lot and 200 from the TopUp lot, leaving the TopUp with 300 (the
  Subscription, which would expire anyway, is spent first)

### Requirement: Promo expiry reclaims unconsumed credits idempotently
`Promo` grants MAY carry `expires_at`. Expired lots SHALL NOT be FIFO-selected. A reclaim sweeper SHALL, in one
transaction with the projection row locked first, post an offsetting `Promo` debit equal to the lot's live `remaining`
(read `FOR UPDATE`, not `original`) carrying `external_ref = "promo-expiry:{lotId}"`, and **only if that debit row was
actually inserted** decrement the projection by that `remaining` and set the lot's `remaining` to 0. The reclaim SHALL
be a complete no-op on re-run and SHALL never reclaim already-consumed credits.

#### Scenario: Unconsumed promo expires
- **GIVEN** a 100-credit Promo lot expiring on day 15, with 30 consumed before expiry
- **WHEN** the reclaim runs after day 15
- **THEN** the remaining 70 is removed from the balance via an offsetting `Promo` debit, the lot `remaining` is 0, and a
  second reclaim run changes nothing

### Requirement: Partner-funded credits are drawable, never customer-billed, and attributed on read
`Partner` grants SHALL create a drawable lot drawn before `Subscription`/`TopUp`. `Partner` draws SHALL be tagged
`CreditSource.Partner` and never `PostPaid`, so they never enter the `Σ |PostPaid|` customer invoice. Attribution SHALL
be computed on read — `GetPartnerAttributionAsync(partnerTenantId, periodStart, periodEnd)` returns `Σ |Partner-source
debits|` over the partner's direct `Customer` children in the half-open window, resolving the owning partner via the
existing `Tenant.ParentTenantId` + `parent.Type == TenantType.Partner` single-hop gate (no `Tenant`-model change). No
materialized `partner_credit_allocation` table SHALL be added in c2 (deferred to the partner-billing change that reads
it).

#### Scenario: Partner credit is attributed without a customer invoice
- **GIVEN** a customer tenant under a `Partner` parent, with a 1000-credit Partner grant, who consumes 600 and has no
  customer overage
- **WHEN** the partner attribution is read for the period
- **THEN** the customer-owed amount is 0 (no `PostPaid` debits) and `GetPartnerAttributionAsync` for the owning partner
  returns 600 — with no invoice and no materialized table involved

### Requirement: Per-source remaining excludes expired and PostPaid
`GetRemainingBySourceAsync` SHALL return the open `remaining` per `CreditSource`, excluding expired Promo lots and the
`PostPaid` source (which has no lot). The sum across sources SHALL equal `tenant_credit_balance.balance`.

#### Scenario: Per-source remaining reconciles to the balance
- **GIVEN** a tenant with 200 open Promo (unexpired), 800 open TopUp, and a fully-consumed Subscription lot
- **THEN** `GetRemainingBySourceAsync` reports Promo 200, TopUp 800, Subscription 0, and the total 1000 equals the
  projection balance

### Requirement: Operator-minted Promo and Partner grants
Promo and Partner grants SHALL be operator-minted via endpoints under the `PlatformAdminOnly` group with the
`CreditGrantGate` policy (`PlatformAdminRequirement("billing:credits:grant")`), idempotent on a caller-supplied key
mapped to `external_ref`. No new RBAC permissions or role-template seeding SHALL be added in c2.

#### Scenario: Operator mints a promo grant idempotently
- **GIVEN** an operator POSTs a Promo grant for a tenant with amount 100, an expiry, and an idempotency key
- **WHEN** the same request is retried
- **THEN** exactly one `Promo` grant + one lot exist (the retry is a no-op on `external_ref`), and a management key
  lacking `billing:credits:grant` is rejected
