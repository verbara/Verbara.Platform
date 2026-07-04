---
tier: GRANDE
owner: maintainer
approver: maintainer
stakeholder: Platform team
decision_ref: Platform/ADR-0032
---

# Typification Platform-Managed LLM

## Purpose

Living capability spec for the Verbara-operated, metered, gated, and quota-capped Typification
LLM that shipped in **Platform v2.15.0** (Typification P2c.2). A tenant MAY opt its Typification AI
provider from its own key (`Byo`) to Verbara's operator-managed LLM (`PlatformManaged`); platform-managed
consumption is metered in tokens, commercialized as AI Credits, gated by a per-tenant plan entitlement,
and capped by a monthly credit allowance. AI remains strictly opt-in: every "cannot serve platform AI"
state degrades to the empty suggestion and never surfaces an error to the agent (the single, tenant-opted-in
exception being a HardBlock 402).
## Requirements
### Requirement: AI source selection on the tenant LLM config

A tenant's Typification LLM configuration SHALL carry an `AiSource` discriminator with values
`Byo` (0) and `PlatformManaged` (1). The default value MUST be `Byo`. `Byo` resolves the tenant's
own encrypted provider key; `PlatformManaged` resolves a Verbara-operated provider built from the
host-bound `PlatformLlmOptions`. `AiSource` is distinct from the provider *family* (`ProviderType`):
it expresses *ownership* of the key, not the provider implementation.

#### Scenario: Default source is BYO

- **GIVEN** a tenant that has never set an `AiSource` on its LLM configuration
- **WHEN** the configuration is read
- **THEN** `AiSource` is `Byo`
- **AND** classification resolves the tenant's own (BYO) provider key, never the Verbara operator key

#### Scenario: AI source round-trips on PUT then GET

- **GIVEN** an entitled tenant that issues `PUT /admin/ai/llm-config` with `aiSource = PlatformManaged`
- **WHEN** the tenant subsequently issues `GET /admin/ai/llm-config`
- **THEN** the response reports `aiSource = PlatformManaged`
- **AND** the operator key/model are never present in the response (only BYO `KeySet`/`KeyLast4` masking is surfaced)

### Requirement: Opt-in to platform-managed AI is entitlement-gated

Transitioning a tenant's `AiSource` to `PlatformManaged` SHALL require the `PlanFeature.PlatformLlm`
entitlement. When the entitlement is absent, the `PUT /admin/ai/llm-config` request that sets
`aiSource = PlatformManaged` MUST be rejected with HTTP 403 and the configuration MUST NOT be mutated.
The entitlement is checked inline via `IFeatureGateService.IsFeatureEnabled`; it stacks under the
Pro license flag `TypificationAi` (operator layer) and the RBAC permission `typification:ai:configure`
(user layer). Platform-type tenants bypass the per-tenant entitlement (existing FeatureGate behavior).

#### Scenario: Opt-in rejected without the PlatformLlm entitlement

- **GIVEN** a tenant WITHOUT the `PlanFeature.PlatformLlm` entitlement
- **WHEN** the tenant issues `PUT /admin/ai/llm-config` with `aiSource = PlatformManaged`
- **THEN** the request is rejected with HTTP 403
- **AND** the persisted `AiSource` remains unchanged (still `Byo`)

#### Scenario: Opt-in allowed with the PlatformLlm entitlement

- **GIVEN** a tenant WITH the `PlanFeature.PlatformLlm` entitlement and the `typification:ai:configure` permission
- **WHEN** the tenant issues `PUT /admin/ai/llm-config` with `aiSource = PlatformManaged`
- **THEN** the request succeeds
- **AND** the persisted `AiSource` becomes `PlatformManaged`
- **AND** any previously stored BYO key fields are ignored/cleared for the platform-managed source

### Requirement: Platform provider resolution is host-bound and fail-closed

When a tenant's `AiSource` is `PlatformManaged`, the resolver SHALL build the effective provider from
the host-bound `PlatformLlmOptions` (operator `BaseUrl`, `ApiKey`, `Model`). The operator `ApiKey`
MUST live only in `PlatformLlmOptions`: it MUST NOT be persisted per-tenant, returned in any DTO, or
logged. When the operator master switch `PlatformLlmOptions.Enabled` is `false`, the resolver MUST
fail closed — it returns no provider (`AiMode.Off`) and the tenant degrades to the empty suggestion
rather than erroring. The provider-resolution cache fingerprint MUST incorporate `AiSource` and a
platform-options version token so that an operator key/model rotation evicts cached resolutions.

#### Scenario: Fail-closed when the operator switch is off

- **GIVEN** a `PlatformManaged` tenant and `PlatformLlmOptions.Enabled = false`
- **WHEN** an AI-suggestion classify is requested for one of that tenant's conversations
- **THEN** the resolver returns no provider (degrades to `AiMode.Off`)
- **AND** the endpoint responds with the empty suggestion (HTTP 200), never an error

#### Scenario: Operator key is never serialized

- **GIVEN** a `PlatformManaged` tenant with a configured operator key in `PlatformLlmOptions`
- **WHEN** the tenant reads `GET /admin/ai/llm-config` or `GET /admin/ai/credits`
- **THEN** neither response contains the operator `ApiKey`, `BaseUrl`, or `Model`

### Requirement: Platform-managed classify is metered in tokens

On a successful platform-managed classify that returns a usage block, the system SHALL record exactly
one durable `UsageRecord` with `UsageType.AiAnalysis`, `Unit = UsageUnit.Tokens`, and
`Quantity = TotalTokens`. The record's `Metadata` MUST carry `inputTokens`, `outputTokens`, and `model`.
The record's `ReferenceId` MUST be the conversation id. When the provider returns no usage block, or the
total token count is non-positive, no `UsageRecord` is written. Metering is recorded regardless of the
surfaced automation band/mode (the LLM call happened, so its real cost is captured).

#### Scenario: Metering a platform-managed classify

- **GIVEN** a `PlatformManaged` tenant within its credit allowance whose provider returns usage of 300 prompt tokens and 100 completion tokens (400 total) on model `gpt-x`
- **WHEN** the classify completes successfully
- **THEN** exactly one `UsageRecord` is written with `UsageType = AiAnalysis`, `Unit = Tokens`, `Quantity = 400`
- **AND** `Metadata["inputTokens"] = "300"`, `Metadata["outputTokens"] = "100"`, `Metadata["model"] = "gpt-x"`
- **AND** `ReferenceId` equals the classified conversation id

#### Scenario: No usage block records nothing

- **GIVEN** a `PlatformManaged` tenant whose provider returns a classification with no usage block (or zero total tokens)
- **WHEN** the classify completes
- **THEN** no `AiAnalysis` `UsageRecord` is written for that call

### Requirement: AI Credits are derived by aggregation, never per-call

AI Credits SHALL be a derived commercial unit computed by aggregation over a period:
`credits = Σ(tokens over period) ÷ CreditTokenRatio` (ratio from `PlatformLlmOptions`, default 1000).
The system MUST NOT round individual calls up to a whole credit — a small call (e.g. 100 tokens) MUST NOT
be billed as a full credit. Tokens are the technical/stored unit; credits are derived only at the
quota/usage/invoice layer.

#### Scenario: Sub-ratio call does not round up to a full credit

- **GIVEN** `CreditTokenRatio = 1000` and a single metered call of 100 tokens in the period
- **WHEN** consumed credits are computed for the period
- **THEN** the aggregate consumed-credit total is `floor(100 ÷ 1000) = 0` credits (no per-call round-up to 1)

#### Scenario: Credits aggregate across multiple records

- **GIVEN** `CreditTokenRatio = 1000` and three metered calls of 400, 400, and 400 tokens in the period
- **WHEN** consumed credits are computed for the period
- **THEN** the aggregate is computed from the summed tokens `1200 ÷ 1000 = 1` credit (not `0 + 0 + 0` from per-call flooring)

### Requirement: BYO is never metered to Billing

A tenant whose `AiSource` is `Byo` (or that has no LLM configuration) SHALL NOT have its classify
calls metered to Billing and SHALL NOT be quota-gated by the AI-credit allowance. BYO tenants pay
their own provider directly; their key path, masking, and provider test endpoint behave exactly as
before P2c.2. The platform-managed quota pre-check and credit metering hooks MUST be skipped entirely
for BYO.

#### Scenario: BYO classify writes no Billing usage

- **GIVEN** a `Byo` tenant whose provider returns a usage block on a successful classify
- **WHEN** the classify completes
- **THEN** no `AiAnalysis` `UsageRecord` is written to Billing
- **AND** no AI-credit quota pre-check is performed for the request

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

### Requirement: AI remains strictly opt-in and never errors the agent

AI assistance SHALL remain strictly optional. Any state in which platform AI cannot be served —
no LLM configuration, AI disabled for the schema/binding, `AiMode.Off`, operator switch off, resolver
returns no provider, provider runtime failure, or a SoftBlock over allowance — MUST degrade to the
empty suggestion (HTTP 200) and MUST NOT surface an error to the agent. The HardBlock 402 is the single,
tenant-opted-in exception to this rule. The deterministic P0/P1 disposition floor never requires an LLM.

#### Scenario: AI disabled degrades to empty suggestion

- **GIVEN** a tenant whose effective AI config is disabled or `Mode == Off`
- **WHEN** an AI-suggestion classify is requested
- **THEN** the endpoint responds with the empty suggestion (HTTP 200), never an error

#### Scenario: Provider runtime failure degrades to empty suggestion

- **GIVEN** a `PlatformManaged` tenant whose classify call fails at the provider (the classifier returns null)
- **WHEN** an AI-suggestion classify is requested
- **THEN** the endpoint responds with the empty suggestion (HTTP 200), never an error

### Requirement: Tenant credit-usage readout

The system SHALL expose `GET /admin/ai/credits` (AdminOnly + operational-tenant, additionally requiring
`typification:ai:configure`) returning the current calendar-month AI-credit position for the tenant:
`allowanceCredits` (nullable — `null` = unlimited), `consumedCredits` (Σ period tokens ÷ `CreditTokenRatio`,
floored), `remainingCredits` (allowance − consumed, floored at 0, or `null` when unlimited),
`usagePercent` (consumed ÷ allowance × 100, or 0 when unlimited/zero), `periodEnd` (exclusive first
instant of next month, UTC), and `actionOnExhaustion` (the tenant's `QuotaAction` name). The operator
key/model MUST NOT appear in this response. The response DTO MUST be registered in `ApiJsonContext`
(AOT — no reflection).

#### Scenario: Credits readout for a tenant with a finite allowance

- **GIVEN** a tenant with `AiCreditsMonthly = 10`, `CreditTokenRatio = 1000`, and 4000 consumed tokens this calendar month
- **WHEN** the tenant issues `GET /admin/ai/credits`
- **THEN** the response reports `allowanceCredits = 10`, `consumedCredits = 4`, `remainingCredits = 6`, `usagePercent = 40`
- **AND** `actionOnExhaustion` is the tenant's `QuotaAction` name (e.g. `SoftBlock`)
- **AND** `periodEnd` is the first instant of the next calendar month (UTC)

#### Scenario: Credits readout for an unlimited tenant

- **GIVEN** a tenant with `AiCreditsMonthly = null` (unlimited)
- **WHEN** the tenant issues `GET /admin/ai/credits`
- **THEN** the response reports `allowanceCredits = null`, `remainingCredits = null`, and `usagePercent = 0`
- **AND** `consumedCredits` still reflects the period's Σtokens ÷ ratio

### Requirement: Runtime entitlement re-check for PlatformManaged tenants
The typification classify endpoint (`ConversationEndpoints.GetTypificationSuggestion`) SHALL verify that `PlanFeature.PlatformLlm` is currently enabled for any tenant whose `TenantLlmConfig.AiSource == PlatformManaged`, on every classify request, **before** the platform-managed quota pre-check and the classifier call. The opt-in `PUT /admin/ai/llm-config` gate MUST NOT be the sole enforcement point; entitlement can be revoked outside the LLM config flow (plan downgrade, add-on expiry, partner-customer suspension, dunning). Enforcement is placed at the classify endpoint — the only runtime consumer that resolves and meters the platform-managed LLM — not in `DefaultLlmProviderResolver` (see ADR-0032 for the seam rationale).

#### Scenario: Entitled PlatformManaged tenant classifies normally
- **GIVEN** a tenant with `AiSource.PlatformManaged`, `PlanFeature.PlatformLlm` enabled, `PlatformLlmOptions.Enabled` true, and AI mode not Off
- **WHEN** the classify endpoint runs for that tenant
- **THEN** the entitlement check passes, the quota pre-check and classifier run as before, and no degrade event is emitted

#### Scenario: PlatformManaged tenant loses entitlement — classify degrades
- **GIVEN** a tenant with `AiSource.PlatformManaged` stored in config, AI mode not Off, but `PlanFeature.PlatformLlm` no longer enabled (plan downgrade / add-on expiry)
- **WHEN** the classify endpoint runs for that tenant
- **THEN** the endpoint SHALL short-circuit before the quota pre-check and the classifier call, returning the existing `EmptySuggestion` (degrade-to-empty, equivalent to `AiMode.Off`)
- **THEN** no AI-Credit usage SHALL be recorded for that classify call
- **THEN** a structured audit event `typification.ai.platformllm.entitlement_missing` SHALL be emitted with `tenantId` and `aiSource`

#### Scenario: BYO tenant is unaffected by entitlement re-check
- **GIVEN** a tenant with `AiSource.Byo` (or no config) in its stored config
- **WHEN** the classify endpoint runs
- **THEN** the `PlanFeature.PlatformLlm` check SHALL NOT be applied (the block is guarded by `isPlatformManaged`) and BYO classification proceeds unchanged, never quota-gated nor metered

### Requirement: Degrade-to-empty on missing PlatformManaged entitlement
The system SHALL apply fail-closed degrade-to-empty behavior — identical to the `AiMode.Off` / "no config" path — when a `PlatformManaged` tenant is missing `PlanFeature.PlatformLlm` at runtime. The degrade MUST be invisible to the agent: no error status, no exception, no AI output, and specifically NOT an HTTP 402 (the entitlement check runs before the quota pre-check, so a downgraded-and-exhausted tenant degrades cleanly rather than receiving a Payment-Required error).

#### Scenario: Agent receives empty suggestion on degraded classify
- **GIVEN** a `PlatformManaged` tenant without current `PlanFeature.PlatformLlm` entitlement
- **WHEN** an agent triggers the classify route
- **THEN** the response SHALL be the existing `EmptySuggestion` result
- **THEN** the HTTP status SHALL be 200 (not 402 or 5xx)
- **THEN** no exception SHALL propagate to the agent UI

#### Scenario: Metric emitted on every degraded classify
- **GIVEN** a `PlatformManaged` tenant degraded due to missing entitlement
- **WHEN** the classify path returns `EmptySuggestion` due to the entitlement re-check
- **THEN** the counter `platformllm.degrade.entitlement_missing` (on meter `verbara.platform.typification.ai`, owned by `TypificationAiMetrics`) SHALL be incremented

### Requirement: Grandfather-vs-immediate-cutoff product decision recorded
The product team SHALL record a durable decision specifying whether entitlement revocation triggers immediate cutoff (option A) or a defined grace window (option B). Option A (immediate cutoff) is APPROVED and recorded in ADR-0032. Because `FeatureGateCache` has no TTL and is repopulated per request by `TenantStatusMiddleware` (and explicitly evicted on plan change / suspension / dunning), "immediate" means the next classify request after the entitlement change — there is no TTL to wait out.

#### Scenario: Immediate cutoff after plan feature revocation (option A — approved)
- **GIVEN** `PlanFeature.PlatformLlm` is revoked for a tenant at plan-change time (cache evicted / repopulated as Starter)
- **WHEN** the next classify request for that tenant runs
- **THEN** it SHALL degrade to empty with no grace window
- **THEN** the `TenantLlmConfig.AiSource` row value SHALL NOT be mutated by the revocation (re-entitlement restores service without reconfiguration)

#### Scenario: Re-entitled tenant automatically recovers
- **GIVEN** a tenant that was previously degraded due to missing entitlement
- **WHEN** `PlanFeature.PlatformLlm` is re-enabled and the per-request `FeatureGateCache` reflects it
- **THEN** the classify endpoint SHALL resume normal platform-managed classification with no admin reconfiguration required

### Requirement: No double-billing after entitlement revocation
After `PlanFeature.PlatformLlm` is revoked for a `PlatformManaged` tenant, `ITypificationCreditMeter.RecordAsync` MUST NOT be called for classify calls that degrade due to the missing entitlement, and `IQuotaEnforcementService.CheckQuotaAsync` MUST NOT be invoked for that call (the entitlement check precedes both). Billing attribution to non-entitled tenants SHALL be prevented.

#### Scenario: No usage recorded on degraded classify
- **GIVEN** a `PlatformManaged` tenant with no current `PlanFeature.PlatformLlm` entitlement
- **WHEN** a classify call degrades to empty due to the entitlement re-check
- **THEN** `ITypificationCreditMeter.RecordAsync` SHALL NOT be called for that call
- **THEN** `IQuotaEnforcementService.CheckQuotaAsync` SHALL NOT be called for that call

#### Scenario: Usage still recorded for entitled PlatformManaged classify
- **GIVEN** a `PlatformManaged` tenant with current `PlanFeature.PlatformLlm` entitlement
- **WHEN** a classify call succeeds and the classifier returns usage
- **THEN** `ITypificationCreditMeter.RecordAsync` SHALL be called with the prompt/completion/total tokens and model from the classification usage

## Architectural Risk

**Level:** MEDIUM — this capability sits on the classify hot path and feeds the money path.

**Affected:** `Verbara.Platform.Llm` (AiSource + platform provider resolution), `Verbara.Platform.Core`
(`PlanFeature.PlatformLlm` + FeatureGate cache), `Verbara.Platform.Typification` (credit-meter hook),
`Verbara.Platform.Billing` (AiAnalysis metering, allowance, quota AI branch), `Verbara.Platform.Api`
(admin opt-in + `GET /admin/ai/credits` DTOs), and the Web readout (see `ai-credits-readout`).

**Mitigation:** strictly opt-in and fail-closed — every "cannot serve" state degrades to the empty
suggestion (the lone tenant-opted-in HardBlock 402 excepted); BYO fully bypasses metering/quota;
credits are aggregate-derived (never per-call round-up); the operator key is host-bound and never
persisted, returned, or logged; enforcement is exact across AOT replicas (durable Postgres usage
records on the legacy path, the ledger projection once the enforcement flag is ON — see
`ai-credit-ledger`).
