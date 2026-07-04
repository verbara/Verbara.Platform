# typification-platform-llm — Delta

## MODIFIED Requirements

### Requirement: Monthly AI-credit allowance is enforced before classify

A monthly AI-credit allowance SHALL be expressed as `TenantQuota.AiCreditsMonthly` (nullable; `null`
means unlimited / pay-as-you-go). For `PlatformManaged` tenants only, the system MUST run an
`IQuotaEnforcementService.CheckQuotaAsync(tenant, UsageType.AiAnalysis, ...)` pre-check BEFORE invoking
`ClassifyAsync`.

This requirement specifies the **legacy consumption account**, which applies only while the
credit-ledger **enforcement flag is OFF**: the service derives consumption by summing the period's
`UsageRecord` tokens from Postgres (exact across AOT replicas) and comparing against the
token-equivalent threshold `AiCreditsMonthly × CreditTokenRatio` (or the differentiated-credit basis
per `ai-credit-metering` when per-direction ratios are active). When the enforcement flag is **ON**,
the quota decision is owned by the `ai-credit-ledger` capability (O(1) projection balance +
`QuotaOutcome`) and this `UsageRecord`-sum account SHALL NOT decide the outcome.

The check maps to a `QuotaAction`:

- **Warn** — the check is `Allowed`; the classify proceeds (a metric/audit MAY be emitted).
- **SoftBlock** (default at/over limit) — the check is not `Allowed`; the classify is degraded to the
  empty suggestion (the AI opt-in floor), and the LLM is not called.
- **HardBlock** (tenant opt-in) — the check is not `Allowed`; the AI route responds with HTTP 402.

An unlimited allowance (`null`) MUST always permit the classify.

#### Scenario: Warn proceeds with classify

- **GIVEN** a `PlatformManaged` tenant whose period consumption is at a Warn level (below the limit) with `QuotaAction = Warn`
- **WHEN** an AI-suggestion classify is requested
- **THEN** the quota pre-check reports `Allowed`
- **AND** the classify proceeds to call the LLM

#### Scenario: SoftBlock degrades to empty suggestion

- **GIVEN** a `PlatformManaged` tenant at/over its `AiCreditsMonthly` allowance with `QuotaAction = SoftBlock`
- **WHEN** an AI-suggestion classify is requested
- **THEN** the quota pre-check reports not `Allowed`
- **AND** the endpoint responds with the empty suggestion (HTTP 200) without calling the LLM
- **AND** no further `AiAnalysis` usage is recorded for that request

#### Scenario: HardBlock returns 402

- **GIVEN** a `PlatformManaged` tenant at/over its `AiCreditsMonthly` allowance with `QuotaAction = HardBlock`
- **WHEN** an AI-suggestion classify is requested
- **THEN** the quota pre-check reports not `Allowed`
- **AND** the endpoint responds with HTTP 402 (Payment Required)

#### Scenario: Unlimited allowance always proceeds

- **GIVEN** a `PlatformManaged` tenant with `AiCreditsMonthly = null` (unlimited)
- **WHEN** an AI-suggestion classify is requested
- **THEN** the quota pre-check reports `Allowed` regardless of accumulated consumption
- **AND** the classify proceeds and is metered as usual

#### Scenario: Enforcement flag ON defers the decision to the credit ledger

- **GIVEN** the credit-ledger enforcement flag is ON for a `PlatformManaged` tenant
- **WHEN** the AiAnalysis quota pre-check runs
- **THEN** the outcome is produced per the `ai-credit-ledger` capability (projection balance + `QuotaOutcome`) and the `UsageRecord` token sum of this requirement does not decide it
