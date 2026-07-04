# ai-credit-ledger Specification

## Purpose
The authoritative accounting substrate for AI credits: an append-only signed ledger with an O(1)
balance projection and per-grant FIFO lots that — behind the cutover flags — own the AiAnalysis
quota decision and the customer-owed overage computation (`Σ |PostPaid debits|`). Covers idempotent
grant/debit posting, the recurring subscription mint, operator-minted TopUp/Promo/Partner grants,
lot expiry (no-carryover + promo), partner attribution on read, and the tenant balance/entries read
API. Shipped across the credit-ledger train (substrate → cutover → top-ups → lots), Platform
v2.16.0; authoritative decisions in Platform/ADR-0033 and its addenda.
## Requirements
### Requirement: Append-only signed credit ledger
The system SHALL persist AI credits as an append-only `ai_credit_ledger` of immutable signed entries. Each
entry SHALL carry `entry_type` (Grant/Debit) as a `SMALLINT` ordinal, a `source` (Subscription, TopUp,
Promo, Partner, PostPaid) as a `SMALLINT` ordinal, a signed `amount NUMERIC(18,6)` (grants positive, debits
negative), an optional `period_key` (`"yyyy-MM"` UTC), an optional `external_ref`, an optional `expires_at`,
an optional `usage_record_id` back-reference, and `created_at`. Identifiers SHALL be `TEXT` (EntityId hex),
matching the schema convention. Entries SHALL never be updated or deleted; corrections are offsetting
entries.

#### Scenario: Ledger entry is immutable and append-only
- **GIVEN** a persisted `CreditLedgerEntry`
- **WHEN** any correction is required
- **THEN** the system SHALL append an offsetting entry; the original entry SHALL remain unchanged

#### Scenario: Ledger SUM equals the projection balance
- **GIVEN** a tenant with any sequence of grant and debit entries
- **WHEN** the ledger `SUM(amount)` (over live, non-expired entries) is computed offline
- **THEN** it SHALL equal the tenant's `tenant_credit_balance.balance`

### Requirement: O(1) balance projection with atomic guarded debit
The system SHALL maintain a `tenant_credit_balance` projection row `(tenant_id PK, balance NUMERIC(18,6),
version BIGINT, updated_at)` updated in the **same transaction** as every ledger entry. A grant SHALL apply
unconditionally (`balance += amount`). A debit SHALL be applied by a single guarded statement
`UPDATE tenant_credit_balance SET balance = balance − @debit, version = version + 1 WHERE tenant_id = @t AND
balance >= @debit`; the ledger debit row SHALL be inserted in the same transaction. The balance read used on
the request path SHALL be an O(1) primary-key lookup of the projection, never a `SUM` aggregate over the
ledger.

#### Scenario: Debit within balance is posted atomically
- **GIVEN** a tenant whose projection balance is 300 credits
- **WHEN** a debit of 100 credits is posted
- **THEN** the ledger gains a −100 debit entry, the projection balance becomes 200, both in one transaction, and the store returns `Posted`

#### Scenario: Debit exceeding balance is rejected, nothing is written
- **GIVEN** a tenant whose projection balance is 50 credits
- **WHEN** a debit of 100 credits is attempted
- **THEN** the guarded `UPDATE` affects 0 rows, no ledger entry is written, the balance remains 50, and the store returns `RejectedInsufficientBalance`

#### Scenario: Concurrent debits cannot drive the balance negative
- **GIVEN** a tenant whose projection balance is 5 credits and two concurrent debits of 5 credits each
- **WHEN** both are posted concurrently
- **THEN** exactly one SHALL return `Posted` (balance 0) and the other `RejectedInsufficientBalance`; the balance SHALL never become negative

### Requirement: Idempotent grant posting
A grant carrying a `period_key` (subscription) SHALL be idempotent on `(tenant_id, period_key, entry_type)`
via a partial unique index and `INSERT … ON CONFLICT DO NOTHING`; a grant carrying an `external_ref`
(top-up) SHALL be idempotent on `(tenant_id, external_ref)`. A duplicate grant SHALL be a no-op that neither
double-inserts nor double-credits the projection.

#### Scenario: Duplicate subscription grant for the same period is a no-op
- **GIVEN** a Subscription grant for `(tenant T, period "2026-06")` already exists
- **WHEN** the same `(tenant, period)` grant is posted again
- **THEN** no second ledger row is inserted and the projection balance is unchanged

### Requirement: Canonical billing-period helper
The system SHALL expose a single `BillingPeriod.Current(IClock)` returning the UTC calendar-month boundary
`[firstOfMonthUtc, firstOfNextMonthUtc)` and the `"yyyy-MM"` period key. All sites that currently compute
the month boundary (quota enforcement, the credit meter, the credits readout, metering summary) SHALL call
this helper. This change SHALL NOT alter any computed boundary — it only removes duplication.

#### Scenario: Helper matches the existing UTC boundary
- **GIVEN** `IClock.UtcNow` = `2026-06-15T12:00:00Z`
- **WHEN** `BillingPeriod.Current(clock)` is evaluated
- **THEN** it SHALL return start `2026-06-01T00:00:00Z`, end `2026-07-01T00:00:00Z`, key `"2026-06"` — identical to the previously inlined computation

### Requirement: Substrate is inert (no behaviour change)
This change SHALL NOT cause any quota decision, metered debit, invoice amount, or API response to differ
from current `main`. Nothing reads or writes the ledger at runtime yet; the store and projection exist and
are unit-tested in isolation. Characterization tests SHALL pin the current `CheckQuotaAsync` and
`BuildAiCreditLineItemAsync` outputs byte-for-byte so the subsequent cutover (change b) can prove
equivalence.

#### Scenario: No runtime path writes the ledger in this change
- **GIVEN** the substrate is deployed
- **WHEN** an AI classification is metered and an invoice is generated
- **THEN** the results SHALL be identical to pre-deployment `main`, and `ai_credit_ledger` SHALL remain empty for that tenant

### Requirement: Metered consumption is a two-step covered-plus-PostPaid debit
The ledger SHALL expose a metered-debit primitive `PostMeteredDebitAsync(tenantId, debit, coveredSource, usageRecordId, ct)`
that, in a **single transaction**, draws `covered = min(balance, debit)` from the prepaid stock via the
guarded `UPDATE tenant_credit_balance SET balance = balance − @covered WHERE tenant_id = @t AND balance >= @covered`
(the projection SHALL floor at 0 — the prepaid lot is never overdrawn) recording a debit row with
`source = @coveredSource`, and SHALL post any uncovered remainder `tail = debit − covered` as an
**unconditional** debit row with `source = PostPaid` that does **not** modify the projection. The existing
guarded `TryPostDebitAsync` SHALL be corrected so a debit records the lot it drew from (it MUST NOT hard-code
`source = PostPaid` for a covered draw).

#### Scenario: Debit fully covered by prepaid balance
- **GIVEN** a tenant with projection balance 10 credits and an incoming debit of 4 credits
- **WHEN** `PostMeteredDebitAsync(tenant, 4, Subscription, …)` runs
- **THEN** the projection balance becomes 6, one debit row of −4 with `source = Subscription` is written, and the result reports covered 4 / postPaid 0

#### Scenario: Debit overflows into PostPaid tail
- **GIVEN** a tenant with projection balance 3 credits and an incoming debit of 5 credits
- **WHEN** `PostMeteredDebitAsync(tenant, 5, Subscription, …)` runs
- **THEN** the projection balance becomes 0, a −3 `Subscription` debit and a −2 `PostPaid` debit are written, and the result reports covered 3 / postPaid 2

#### Scenario: Concurrent metered debits never overdraw the prepaid lot
- **GIVEN** two concurrent `PostMeteredDebitAsync` calls of 3 credits each against a balance of 4
- **WHEN** both commit
- **THEN** the projection balance is 0 (never negative), the total covered across both is exactly 4, and the remaining 2 credits are recorded as `PostPaid` tail

### Requirement: Enforcement and metering read the ledger behind a cutover flag
Behind a single **enforcement** feature flag (default off), the system SHALL derive credits-left and
credits-consumed for `UsageType.AiAnalysis` quota enforcement and metering from the credit ledger (the O(1)
projection balance and `PostMeteredDebitAsync`), and SHALL NOT recompute them from `usage_records` against
the `AiCreditsMonthly` scalar. When the flag is **off** the legacy `usage_records` path SHALL run unchanged.
For an allowance-only tenant the observable outputs (quota decision, metered credits, and — once the invoice
flag flips — invoiced overage) SHALL equal the pre-cutover behaviour the change-(a) characterization tests
pin for the same consumed/allowance inputs.

#### Scenario: Flag off preserves the legacy path exactly
- **GIVEN** the enforcement flag is off
- **WHEN** an AiAnalysis quota check and metering run
- **THEN** they behave identically to #4 (no ledger read or debit) and the legacy characterization tests pass unchanged

#### Scenario: Allowance-only tenant behaves identically with the flag on
- **GIVEN** the enforcement flag is on and a tenant with `AiCreditsMonthly = 1000` seeded into the ledger, 1350 credits consumed in the period
- **WHEN** quota is checked and (after the invoice flag flips) the invoice is generated
- **THEN** the quota decision and the invoiced overage equal the change-(a) characterization values for the same inputs (overage 350)

### Requirement: Structured quota outcome drives the decision; Warn overflows, never hard-blocks at zero
`QuotaCheckResult` SHALL expose a `QuotaOutcome { Allow, Warn, SoftBlock, HardBlock }` (4th positional member,
default `Allow`), set in both AiAnalysis branches and the generic path. The enforcement service SHALL be the
sole authority for the outcome. An **exhausted prepaid balance** SHALL yield `SoftBlock` (degrade) or
`HardBlock` (402) **only** for tenants whose `TenantQuota.QuotaAction` is `SoftBlock`/`HardBlock`; a tenant
whose `QuotaAction` is `Warn` SHALL **never** be hard-blocked at zero — it overflows into `PostPaid` and keeps
serving (preserving #4's postpaid overage). The classify endpoint SHALL branch on `Outcome` and SHALL NOT
re-read `QuotaAction` via a second `GetQuotaStatusAsync`. The Allow boundary SHALL be `balance >= projectedDebit`
(`>=`, so "exactly at the limit = allowed").

#### Scenario: Warn tenant at zero balance keeps serving (overage)
- **GIVEN** a ledger tenant whose prepaid balance is 0 and whose `TenantQuota.QuotaAction = Warn`
- **WHEN** an AiAnalysis classification is pre-checked and then metered
- **THEN** `QuotaCheckResult.Outcome = Warn` (Allowed = true), the classification proceeds, and the metered credits are recorded as a `PostPaid` tail (billable overage)

#### Scenario: HardBlock tenant at zero balance is blocked
- **GIVEN** a ledger tenant whose prepaid balance is 0 and whose `TenantQuota.QuotaAction = HardBlock`
- **WHEN** an AiAnalysis classification is pre-checked
- **THEN** `QuotaCheckResult.Outcome = HardBlock` and the endpoint returns 402

#### Scenario: Endpoint branches on Outcome without a second read
- **GIVEN** the classify endpoint receives a `QuotaCheckResult`
- **WHEN** it decides 402 vs degrade vs proceed
- **THEN** it switches on `Outcome` alone and makes no second `GetQuotaStatusAsync` call

### Requirement: Monthly allowance is a recurring subscription grant
`AiCreditsMonthly` SHALL be minted as a recurring `Subscription` grant idempotent on `(tenant_id, period_key)`
with `expires_at = periodEnd` (no carryover). A scheduled mint worker (mirroring `OverageInvoiceIssuanceWorker`
— keyed resilience policy, per-cycle scope, `internal` cycle method) SHALL ensure the grant exists at/after
each UTC month rollover; the current-period back-fill SHALL seed existing tenants before the enforcement flag
is enabled.

#### Scenario: Subscription grant minted once per period
- **GIVEN** a tenant with `AiCreditsMonthly = 1000`
- **WHEN** the mint worker runs twice in the same UTC month
- **THEN** exactly one `Subscription` grant of 1000 (expiring at period end) exists for that period (idempotent via `ON CONFLICT DO NOTHING`)

### Requirement: Invoicing derives overage from PostPaid debits behind a shadow-gated flag
Behind a **separate invoice-read** feature flag (default off), `BuildAiCreditLineItemAsync` SHALL compute
customer-owed AiAnalysis overage as `Σ (period debit rows where source = PostPaid)` and SHALL stop reading
`usage_records` for that amount. Until the flag flips, invoicing SHALL keep computing overage from
`usage_records` (the shadow-reconciliation window). The flag SHALL NOT flip until a production check confirms,
per tenant, that `Σ PostPaid debits == max(0, consumedCredits − allowance)` for the period. The audit invariant
is restated as `balance == max(0, …)` over the prepaid lot — `PostPaid` debits accrue outside the floored
projection.

#### Scenario: Invoiced overage matches the legacy computation at flip time
- **GIVEN** a tenant with `AiCreditsMonthly = 1000` and 1350 credits consumed, seeded into the ledger
- **WHEN** the invoice-read flag is on
- **THEN** the AI line item has `OverageQuantity = 350` and `Amount = 350 × UnitPrice`, equal to the legacy `usage_records` computation for the same inputs

### Requirement: Cutover rollout is flag-gated, back-fill-idempotent, and ratio-frozen
The cutover SHALL be deployable with all code paths default-off. The enforcement flag SHALL NOT be enabled
until the current-period back-fill is 100% complete **and** at least one mint-worker tick is confirmed for the
current period (so a tenant onboarded in the gap is not falsely blocked at balance 0). The back-fill SHALL be
idempotent — its seed `PostPaid` debit (for a tenant already in overage at back-fill time) SHALL carry
`external_ref = "backfill:{period}"` so re-runs are no-ops via `uq_ai_credit_ledger_extref`. The credit ratios
(`CreditTokenRatio`, `InputCreditTokenRatio`, `OutputCreditTokenRatio`) SHALL be frozen across the
back-fill→flip window, and the back-fill SHALL reconstruct `consumedSoFar` on the same `PerDirectionActive`
basis the runtime meter will use.

#### Scenario: Back-fill re-run is a no-op
- **GIVEN** the current-period back-fill has run once for a tenant already in overage
- **WHEN** it runs again (e.g. a redeploy)
- **THEN** no duplicate grant or debit is written (idempotent via the period-key and `backfill:{period}` external-ref unique indexes)

#### Scenario: Tenant already in overage at back-fill time
- **GIVEN** a tenant with `AiCreditsMonthly = 1000` who has consumed 1350 credits this period before cutover
- **WHEN** the back-fill seeds the ledger
- **THEN** a `Subscription` grant of 1000, a covered debit of −1000 (`Subscription`), and a `PostPaid` debit of −350 are written; the projection balance is 0 and `Σ PostPaid = 350`

### Requirement: Operator-minted top-up grants
The system SHALL allow an operator (`PlatformAdminOnly`) with permission `billing:credits:grant` to mint a
`TopUp` grant for a tenant via `POST /api/v1/management/credit-ledger/top-up`. (The partner-scoped top-up — with
owning-child validation — is deferred to c2.) The grant SHALL be a positive-amount
`CreditEntryType.Grant` with `CreditSource.TopUp`, idempotent on a caller-supplied idempotency key carried as
`external_ref` (a repeated key SHALL NOT mint a second grant). A caller without `billing:credits:grant` SHALL
receive HTTP 403 and no grant SHALL be created. A non-positive amount SHALL be rejected with HTTP 400.

#### Scenario: Top-up adds fungible balance
- **GIVEN** a tenant with balance 200 credits
- **WHEN** an operator posts a top-up of 500 with idempotency key `txn-abc`
- **THEN** the tenant balance becomes 700 and a `TopUp` grant of 500 with `external_ref = "txn-abc"` exists

#### Scenario: Idempotent top-up
- **GIVEN** a `TopUp` grant with `external_ref = "txn-abc"` already exists for the tenant
- **WHEN** the same idempotency key is posted again
- **THEN** no second grant is inserted and the balance is unchanged

#### Scenario: Unauthorised top-up is rejected
- **GIVEN** a caller without `billing:credits:grant`
- **WHEN** `POST …/credit-ledger/top-up` is called
- **THEN** HTTP 403 is returned and no grant is created

### Requirement: Top-up consumption is correct without lot machinery
A `TopUp` grant SHALL be fungible — consumed by the existing metered-debit covered draw exactly like a
`Subscription` grant — and SHALL NOT change the customer invoice computation. For an allowance-plus-top-up
tenant the invoiced overage SHALL remain `Σ |PostPaid debits|`, and `PostMeteredDebitAsync` SHALL be unchanged
by this change.

#### Scenario: Top-up reduces billable overage
- **GIVEN** a tenant with `AiCreditsMonthly = 1000` who has a 500 `TopUp` and consumes 1200 credits in the period
- **WHEN** the invoice is generated
- **THEN** the `PostPaid` tail (billable overage) is 0 (1000 subscription + 500 top-up cover the 1200) and the customer-owed amount is 0

### Requirement: Tenant balance and entries read API
The system SHALL expose `GET /api/v1/admin/credit-ledger/balance` (the current O(1) projection balance) and
`GET /api/v1/admin/credit-ledger/entries` (paginated, `Core.PagedResult<CreditLedgerEntryDto>`), both requiring
permission `billing:credits:read`, scoped to the calling tenant. The entries response SHALL carry an accurate
`TotalCount` (served by a new `ICreditLedgerStore.GetEntriesCountAsync`). Entry ordering SHALL be
most-recent-first and deterministic across the Postgres and InMemory stores (a stable tiebreak on `entry_id`).

#### Scenario: Tenant reads its own balance
- **GIVEN** a tenant admin with `billing:credits:read` whose balance is 700
- **WHEN** `GET …/credit-ledger/balance` is called
- **THEN** HTTP 200 returns the balance 700 for the calling tenant only

#### Scenario: Paginated entries carry a total count
- **GIVEN** a tenant with 30 ledger entries
- **WHEN** `GET …/credit-ledger/entries?page=1&pageSize=25` is called
- **THEN** the `PagedResult` returns the 25 most-recent entries with `TotalCount = 30` and `TotalPages = 2`

### Requirement: Credit-grant and credit-read permissions
The system SHALL define permissions `billing:credits:grant` and `billing:credits:read`. `billing:credits:read`
SHALL be granted to the operator (`platform_admin`) and tenant-admin role templates (it permits reading one's
own balance). `billing:credits:grant` SHALL be granted to the operator (`platform_admin`) role template **only**
in c1 — it SHALL NOT be granted to tenant `admin`/`system_admin` (so it must NOT be added to
`AllPermissions()`); the `partner_admin` grant lands with the partner-scoped endpoint in c2. Existing tenants
receive `:read` on `platform_admin` via the `RbacReseed` CLI.

#### Scenario: Tenant admin cannot mint credits
- **GIVEN** a tenant `admin` (not platform/partner) without `billing:credits:grant`
- **WHEN** `POST …/credit-ledger/top-up` is called
- **THEN** HTTP 403 is returned

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

### Requirement: Lot expiry reclaims unconsumed credits idempotently
Expired lots SHALL NOT be FIFO-selected — any lot MAY carry `expires_at` (operator-set on `Promo`, period-end on
`Subscription`, which has no carryover per ADR-0033). A reclaim sweeper SHALL, in one
transaction with the projection row locked first, post an offsetting debit **tagged the lot's own source** equal to the
lot's live `remaining` (read `FOR UPDATE`, not `original`) carrying `external_ref = "lot-expiry:{lotId}"`, and **only if
that debit row was actually inserted** decrement the projection by that `remaining` and set the lot's `remaining` to 0.
The reclaim SHALL be a complete no-op on re-run and SHALL never reclaim already-consumed credits. This single mechanism
enforces both subscription no-carryover and promo expiry.

#### Scenario: Unconsumed promo expires
- **GIVEN** a 100-credit Promo lot expiring on day 15, with 30 consumed before expiry
- **WHEN** the reclaim runs after day 15
- **THEN** the remaining 70 is removed from the balance via an offsetting `Promo`-tagged debit, the lot `remaining` is 0,
  and a second reclaim run changes nothing

#### Scenario: Unused subscription does not carry over
- **GIVEN** a Subscription lot expiring at period end with 400 credits unconsumed
- **WHEN** the reclaim runs after the period boundary
- **THEN** the 400 is removed via an offsetting `Subscription`-tagged debit, so the next period starts from the
  freshly-minted subscription grant alone (no carryover)

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

