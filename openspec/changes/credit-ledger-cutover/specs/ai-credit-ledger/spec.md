## ADDED Requirements

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
