# ADR-0033: AI-Credit Ledger — unified ledger + balance projection, sequenced cutover

- **Status:** Accepted
- **Date:** 2026-06-26
- **Supersedes/extends:** the prepaid-credit intent of the `typification-credit-ledger` OpenSpec change (which is restructured into a 3-change program by this ADR).
- **Related:** ADR-0022 (Native AOT, no Dapper), `ai-credit-metering` & `ai-credit-billing` living specs (P2c.2 #3/#4, commit `b51ecaa4`).

## Context

P2c.2 ships a **monthly AI-credit allowance** (`TenantQuota.AiCreditsMonthly`, a `long?` scalar). Quota
enforcement, the metering funnel, and invoice generation all **recompute** consumption from
`usage_records` against that scalar within the UTC calendar month. The roadmap requires prepaid bundles,
one-time top-ups, promotional grants, and partner allocations with a running, carried-over, auditable
balance.

A from-memory proposal framed the choice as three options (full-scope-vs-not · "XOR per tenant" ·
operator-only RBAC). A deep design-space analysis (4 candidate models scored by 3 independent judges + a
completeness critic + a 5-point correctness re-exploration, all grounded against the real code) showed
those were **the wrong axis**. The load-bearing distinction is **stock vs. flow**:

> `AiCreditsMonthly` is a **flow** (a scalar the calendar resets monthly, no carryover, no balance).
> Top-ups / prepaid / promos / partner allocations are **stock** (a persisted balance that expires and is
> consumed in order). You cannot model stock as a scalar. Every roadmap requirement — block at true zero,
> invoice only the post-paid remainder, audit trail, idempotent top-ups, expiry — is a *balance* requirement.

### Rejected: "XOR per tenant" (two parallel models, branch on "has a ledger?")

Disqualified by all three judges (scores 34–38 vs 83–90 for the ledger). It double-counts or drops
consumption at the **mid-period flip** (a tenant who buys their first top-up on day 15 flips the predicate
mid-calendar-month while #4 computes over the whole month), and it **cannot represent** a tenant holding a
subscription allowance **and** a top-up simultaneously — the modal customer. Build cost M, lifetime cost
XL (two enforcement semantics, two invoice paths, a 4-site predicate, a combinatorial test matrix forever,
then the unification anyway).

### Deferred (not rejected): "multi-bucket wallet with pre-LLM reservation/holds"

The richest end-state, but its reservation lifecycle + expiry sweeper + bucket-order policy is machinery
the product has no current requirement for. Its **substrate is identical** to the chosen design; holds and
buckets bolt on later as additive layers if a prepaid-telephony-grade requirement appears.

## Decision

Adopt **one signed, append-only AI-credit ledger as the single source of truth**, with the monthly
allowance modelled as **just another (recurring) grant entry**. There is no "allowance model" vs "ledger
model" to choose between or unify — allowance, top-up, promo, and partner are all **grant entries on one
balance**.

### Architecture

- **`ai_credit_ledger`** (append-only audit truth): signed `amount NUMERIC(18,6)` (grant +, debit −),
  `entry_type SMALLINT`, `source SMALLINT` ∈ {Subscription, TopUp, Promo, Partner, PostPaid},
  `period_key TEXT NULL` (`"yyyy-MM"` UTC, for subscription-grant idempotency), `external_ref TEXT NULL`
  (top-up idempotency), `expires_at TIMESTAMPTZ NULL`, `usage_record_id TEXT NULL` (debit back-ref),
  `created_at`. TEXT ids (EntityId hex), `SMALLINT` enums — matching the schema convention (ADR-0022).
- **`tenant_credit_balance`** (O(1) projection): `(tenant_id TEXT PK, balance NUMERIC(18,6), version BIGINT,
  updated_at)`. **The completeness critic's key correction:** a live `SUM(amount)` on every classify is an
  unbounded, ever-growing aggregate on the hottest path. The projection is authoritative on the request
  path; the ledger remains independently re-derivable (`SUM == balance`) as a cheap offline audit
  assertion.
- **Atomic debit primitive (zero SDK change):** in one `NpgsqlConnection`+`NpgsqlTransaction` (the existing
  `ExecuteAsync(...)→int` overload), `INSERT` the ledger debit **and**
  `UPDATE tenant_credit_balance SET balance = balance − @debit, version = version + 1 WHERE tenant_id = @t
  AND balance >= @debit`. **Rows-affected 0 = insufficient (rollback) / 1 = granted.** Race-free, no app
  lock, no `SUM`. Grants apply unconditionally (no `WHERE balance>=`).
- **Hard-block contract:** add `QuotaOutcome { Allow, Warn, SoftBlock, HardBlock }` to `QuotaCheckResult`.
  The enforcement service becomes the **sole authority** — a depleted balance ⇒ `HardBlock` regardless of
  the config scalar `TenantQuota.QuotaAction`. `ConversationEndpoints` switches on `Outcome` and **drops**
  the brittle second `GetQuotaStatusAsync` re-read (which today silently degrades a zero-balance tenant
  whose configured action is `Warn`/`SoftBlock`). No exceptions — `QuotaExceededException` stays
  nonexistent; quota remains result-based.
- **Period:** canonical UTC calendar month `[firstOfMonthUtc, firstOfNextMonthUtc)` (verified identical in
  5 sites today). Extract one shared `BillingPeriod.Current(IClock)` helper so quota, meter, invoice, and
  the grant-mint agree. Subscription grant minted idempotently per `(tenant_id, period_key)` via
  `INSERT … ON CONFLICT (tenant_id, period_key, entry_type) DO NOTHING` — race-safe under the
  month-rollover thundering herd — driven by a scheduled mint worker (mirrors `OverageInvoiceIssuanceWorker`),
  with the same `ON CONFLICT` making any lazy fallback safe.

### Hardening decisions (from the correctness re-exploration)

1. **Balance read model:** maintained projection from day 1, **not** live `SUM` (no atomic guard exists on a
   pure read; check-then-act races).
2. **Debit→source-lot linkage:** debits record which grant lot they drew from (FIFO over open lots by
   `billable_priority, expires_at, created_at`); the uncovered tail is a synthetic `PostPaid` lot. Invoice
   customer-owed = `Σ allocations to PostPaid lots`; prepaid/promo/partner are never re-billed or
   cross-attributed (partner draws feed the partner-revenue ledger). **Required only when ≥2 sources
   coexist (change c); v1 is single-source and trivial — but the v1 schema reserves `source`/`expires_at`
   so it never needs a backfill.**
3. **Idempotency:** **debits need no key** — the funnel is genuinely fire-and-forget post-LLM, not retried;
   one classify = at most one debit; `conversationId` would be **wrong** (re-classify is a legitimate
   separate charge). **Top-ups** use `external_ref` (partial unique index). A per-classification idempotency
   id is a precondition only for any *future* at-least-once delivery (out of scope).
4. **Consumption order vs atomicity:** v1 = one fungible balance (no 2nd consumable source exists yet);
   ordered multi-lot decrement is the change-(c) fast-follow. The v1 primitive must not foreclose it.
5. **Period/timezone:** UTC year-month, shared helper, idempotent mint (above).

## Delivery — a 3-change program (each leaves `main` shippable)

- **(a) Substrate** — migration 012 (`ai_credit_ledger` + `tenant_credit_balance`), Postgres + InMemory
  store twins, the atomic guarded debit/grant primitive, the shared `BillingPeriod` helper, **and
  characterization tests pinning #4's current quota/invoice numbers byte-for-byte**. Lands **inert**
  (nothing reads the ledger yet). OpenSpec change: `credit-ledger-substrate`.
- **(b) Cutover** — re-point `CheckQuotaAsync`, `BillingTypificationCreditMeter.RecordAsync`, and
  `BuildAiCreditLineItemAsync` onto the projection/ledger; `AiCreditsMonthly` → recurring idempotent
  Subscription grant (scheduled mint worker); add the `QuotaOutcome` contract + endpoint switch;
  **current-period back-fill migration** (highest-risk single step, gated by (a)'s characterization tests +
  a feature flag so the invoice-read flip waits for back-fill completion). Behaviour **byte-identical** for
  allowance-only tenants. OpenSpec change: `credit-ledger-cutover`.
- **(c) Sources** — TopUp/Promo/Partner grant sources + debit→lot allocation + `expires_at` consumption +
  RBAC perms + top-up/balance/entries endpoints. Additive. OpenSpec change: `credit-ledger-sources`.
- **Web** — balance widget on the Platform.Web v3.x train, deferred (precedent: #1/#3 shipped Platform-only;
  #2's Web shipped as its own PR after the API).

## Product-owner decisions (made 2026-06-26)

- **Block-at-zero strictness:** post-LLM exact guarded debit now; **pre-LLM reservation is a deferred
  fast-follow** (an AI-credit product tolerates the ≤1-in-flight-at-exactly-zero and crash-between-LLM-and-
  commit leaks; prepaid telephony would not).
- **Top-up RBAC:** both perms (`billing:credits:grant`, `billing:credits:read`) ship in (c); **`:grant` is
  operator/partner-only** while a payment rail is deferred (top-up mints credits). Tenant-facing surface is
  read-only balance. Self-service later = a role-template flip, not a redesign.
- **Subscription carryover:** monthly subscription grants **expire at period end** (`expires_at = periodEnd`,
  matching today's no-carryover scalar); top-up/promo/partner grants persist. (Default; revisit per product.)

## Consequences

**Positive:** one source of truth (no dual-model debt); O(1) hot-path guard; crash-consistent by
construction; promos/partner/payment-rail become rows, not branches; #4 converges instead of forking; the
one migration is paid once. **Negative / watch:** (b) re-points 2-day-old revenue code — mitigated by
characterization tests landed in (a) and the back-fill feature flag; new RBAC perms reach existing tenants
only via the `RbacReseed` CLI (a deploy step); the current-period back-fill is a one-time migration that
must complete before the invoice-read flip is enabled.

## Deferred fast-follows (named, not dropped)

Pre-LLM reservation/holds; per-classification idempotency id (only if at-least-once delivery is added);
expiry sweeper (reporting only); ordered multi-lot consumption (lands with change c when a 2nd source
ships); a real payment rail (a webhook writing a `+TopUp` entry keyed by `external_ref`).

## Addendum (2026-06-27): Warn-overflow reconciliation — the debit is two-step, not block-at-zero-for-all

Grounding change (b) against the shipped code (a judge-panel + completeness-critic design study) surfaced an
**internal contradiction in this ADR + the (b) spec** that must be resolved authoritatively before (b)
implements. The body above (notably the Decision bullet "a depleted balance ⇒ `HardBlock` regardless of
`TenantQuota.QuotaAction`") reads as **strict prepaid block-at-zero**. But change #4 (shipped `b51ecaa4`) is
**postpaid for `Warn` tenants** — the default action `Warn` *proceeds past the allowance and the excess is
invoiced as overage* — and the (b) spec simultaneously asserts byte-identical preservation of that overage
(scenario: `AiCreditsMonthly=1000`, 1350 consumed ⇒ overage 350). A `Warn` tenant cannot both hard-block at
zero **and** overflow into billable overage. The PO decision (2026-06-27) is **preserve #4 — postpaid for
`Warn`** (no revenue write-off, no behaviour change on flag-flip). The reconciliation:

- **The metered debit is two-step (Model C), in one transaction:** `covered = min(balance, debit)` is drawn
  from the prepaid stock via the guarded `UPDATE … WHERE balance >= @covered` (the projection floors at 0 —
  the prepaid lot stays un-overdrawable, honouring the original block-at-zero intent **for the prepaid
  stock**); the **uncovered remainder** `tail = debit − covered` is posted as an unconditional ledger debit
  row tagged `source = PostPaid`, which does **not** touch the projection. So `block-at-zero` governs the
  *prepaid lot*; `Warn` tenants overflow the tail into `PostPaid`.
- **Quota outcome (corrected):** exhausted **prepaid** balance ⇒ `HardBlock` only for
  `QuotaAction ∈ {SoftBlock→degrade, HardBlock→402}`; a `Warn` tenant is **never hard-blocked at zero** — it
  overflows to `PostPaid` and keeps serving. `QuotaOutcome` still becomes the contract the endpoint switches
  on (dropping the second `GetQuotaStatusAsync` read); the enforcement service is still the sole authority.
- **Invoicing:** customer-owed overage = `Σ (period debit rows where source = PostPaid)`. This is **exactly**
  the change-(c) "Invoice customer-owed = Σ allocations to PostPaid lots" shape at n=1 lots — zero rework.
- **Balance/audit invariant (restated):** the projection is no longer `balance == Σ amount`; it is
  `balance == max(0, Σ amount over non-PostPaid lots − Σ covered draws)` — i.e. the prepaid sub-ledger
  re-derives the projection; `PostPaid` debits accrue *outside* the floored projection as the billable tail.
- **Substrate correctness fix (mandatory, was a latent bug):** (a)'s `TryPostDebitAsync` hard-codes
  `source = PostPaid` for **every** debit. A *covered* prepaid draw must record the lot it drew from
  (`Subscription` in v1). Left unfixed, a `Σ source=PostPaid` invoice over-bills **100% of consumption** for
  every allowance-only tenant. (b) parameterises the debit source (covered ⇒ `Subscription`, tail ⇒ `PostPaid`).
- **Rollout discipline (C-on-D):** (b) lands all code behind feature flags **default-off**, gating **all
  three** read seams (quota, meter, invoice) — not only invoice. Ordering: back-fill 100% **and** one
  confirmed mint-worker tick for the current period ⇒ enable enforcement (quota+meter on ledger) ⇒ run the
  invoice **Σ-PostPaid in shadow** for one billing period, asserting `Σ PostPaid == max(0, consumed −
  allowance)` per tenant ⇒ only then flip the invoice read. The dual computation is a **time-boxed
  reconciliation gate**, never the resting architecture. Back-fill seed debits carry
  `external_ref = "backfill:{period}"` (debits are otherwise un-keyed) so re-runs are no-ops via
  `uq_ai_credit_ledger_extref`. Ratios (`CreditTokenRatio` / `Input` / `Output`) are **frozen** across the
  back-fill→flip window (the back-fill must reconstruct `consumedSoFar` on the same `PerDirectionActive` basis
  the runtime meter will use).
- **Boundary:** `balance >= projectedDebit ⇒ Allow` (use `>=`, matching the existing `<=` so "exactly at the
  limit = allowed"). The flat-path `Reason` string becomes credit-denominated under the ledger (it is
  internal-only — no endpoint echoes it); the change-(a) characterization tests are **re-seeded against the
  ledger** to the same consumed values and assert the same outcomes (task "5.1 stays green unchanged" was
  impossible as written — the tests inject `usage_records` mocks and never seeded a balance).
