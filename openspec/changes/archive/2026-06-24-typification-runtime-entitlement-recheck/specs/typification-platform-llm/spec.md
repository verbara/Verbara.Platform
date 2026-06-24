## ADDED Requirements

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

**Level:** LOW

**Affected:** `Verbara.Platform.Api` (`ConversationEndpoints.GetTypificationSuggestion` — one added `IFeatureGateService` `[FromServices]` param + a guarded entitlement block), `Verbara.Platform.Typification` (`TypificationAiMetrics` — one added counter + `[LoggerMessage]`), Billing accuracy (`UsageRecord` no longer attributed to downgraded tenants). No SDK/Pro changes. No resolver change. No Web changes (disabled toggle already in place via C5). No migration, no new DTO.

**Mitigation:** `IFeatureGateService` is a singleton, synchronous, in-memory lookup already populated per-request by `TenantStatusMiddleware` — no new I/O and no scoping concern when injected. The degrade path reuses the existing `EmptySuggestion` / 200 OK result — no new failure mode. The entitlement block sits before the quota pre-check, guaranteeing the no-402 / no-metering outcomes together. AOT is unaffected: no new reflection, no new serialized DTO. Residual: a future non-typification consumer of platform-managed LLM would not inherit this gate (see ADR-0032 Consequences) and must add its own check.
