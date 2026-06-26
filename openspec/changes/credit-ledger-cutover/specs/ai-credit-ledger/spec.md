## ADDED Requirements

### Requirement: Enforcement, metering and invoicing read the ledger
The system SHALL, after the cutover is deployed and the back-fill has completed, derive credits-left and
credits-consumed for `UsageType.AiAnalysis` quota enforcement, metering, and invoicing from the credit
ledger (via the O(1) balance projection and ledger debits), and SHALL NOT recompute them from
`usage_records` against the `AiCreditsMonthly` scalar. For an allowance-only tenant the observable outputs
SHALL be identical to the pre-cutover behaviour pinned by the change-(a) characterization tests.

#### Scenario: Allowance-only tenant behaves identically after cutover
- **GIVEN** a tenant with `AiCreditsMonthly = 1000`, no top-ups, and 1350 credits consumed in the period
- **WHEN** quota is checked and the invoice is generated post-cutover
- **THEN** the quota decision and the invoiced overage SHALL equal the change-(a) characterization values for the same inputs

### Requirement: Structured quota outcome drives hard-block
`QuotaCheckResult` SHALL expose a `QuotaOutcome { Allow, Warn, SoftBlock, HardBlock }`. The enforcement
service SHALL be the sole authority for the outcome; an exhausted ledger balance SHALL yield `HardBlock`
regardless of the tenant's configured `TenantQuota.QuotaAction`. The classify endpoint SHALL branch on
`Outcome` and SHALL NOT re-read `QuotaAction`.

#### Scenario: Zero-balance hard-blocks regardless of configured action
- **GIVEN** a ledger tenant whose balance is 0 and whose `TenantQuota.QuotaAction = Warn`
- **WHEN** an AiAnalysis classification is pre-checked
- **THEN** `QuotaCheckResult.Outcome = HardBlock` and the endpoint returns 402 (not a silent degrade)

### Requirement: Monthly allowance is a recurring subscription grant
`AiCreditsMonthly` SHALL be minted as a recurring `Subscription` grant idempotent on `(tenant_id,
period_key)` with `expires_at = periodEnd` (no carryover). A scheduled mint worker SHALL ensure the grant
exists at/after each UTC month rollover; a one-time current-period back-fill SHALL seed existing tenants
before the invoice-read flip is enabled.

#### Scenario: Subscription grant minted once per period
- **GIVEN** a tenant with `AiCreditsMonthly = 1000`
- **WHEN** the mint worker runs twice in the same UTC month
- **THEN** exactly one Subscription grant of 1000 (expiring at period end) exists for that period
