## ADDED Requirements

### Requirement: Runtime entitlement re-check for PlatformManaged tenants
`DefaultLlmProviderResolver.ResolveAsync` SHALL verify `PlanFeature.PlatformLlm` is currently enabled for any tenant whose `TenantLlmConfig.AiSource == PlatformManaged`, at every resolve call. The opt-in `PUT /admin/ai/llm-config` gate MUST NOT be the sole enforcement point; entitlement can be revoked outside the LLM config flow (plan downgrade).

#### Scenario: Entitled PlatformManaged tenant resolves normally
- **GIVEN** a tenant with `AiSource.PlatformManaged`, `PlanFeature.PlatformLlm` enabled, and `PlatformLlmOptions.Enabled` true
- **WHEN** `DefaultLlmProviderResolver.ResolveAsync` is called
- **THEN** the resolver returns a valid platform `ILlmProvider` and no degrade event is emitted

#### Scenario: PlatformManaged tenant loses entitlement — resolver returns null
- **GIVEN** a tenant with `AiSource.PlatformManaged` stored in config, but `PlanFeature.PlatformLlm` is no longer enabled (plan downgrade)
- **WHEN** `DefaultLlmProviderResolver.ResolveAsync` is called during a classify request
- **THEN** the resolver SHALL return `null` (degrade-to-empty, equivalent to `AiMode.Off`)
- **THEN** no `UsageRecord(AiAnalysis)` SHALL be recorded for that classify call
- **THEN** a structured audit event `typification.ai.platformllm.entitlement_missing` SHALL be emitted with `tenantId` and `aiSource`

#### Scenario: BYO tenant is unaffected by entitlement re-check
- **GIVEN** a tenant with `AiSource.Byo` in its stored config
- **WHEN** `DefaultLlmProviderResolver.ResolveAsync` is called
- **THEN** the `PlanFeature.PlatformLlm` check SHALL NOT be applied and BYO resolution proceeds unchanged

### Requirement: Degrade-to-empty on missing PlatformManaged entitlement
The system SHALL apply fail-closed degrade-to-empty behavior — identical to the `AiMode.Off` / "no config" path — when a `PlatformManaged` tenant is missing `PlanFeature.PlatformLlm` at runtime. The degrade MUST be invisible to the agent: no error status, no exception, no AI output.

#### Scenario: Agent receives empty suggestion on degraded classify
- **GIVEN** a `PlatformManaged` tenant without current `PlanFeature.PlatformLlm` entitlement
- **WHEN** an agent triggers the classify route
- **THEN** the response SHALL be the existing `EmptySuggestion` result
- **THEN** the HTTP status SHALL be 200 (not 402 or 5xx)
- **THEN** no exception SHALL propagate to the agent UI

#### Scenario: Metric emitted on every degraded classify
- **GIVEN** a `PlatformManaged` tenant degraded due to missing entitlement
- **WHEN** the classify path returns `EmptySuggestion` due to the entitlement re-check
- **THEN** a counter metric `typification.ai.platformllm.degrade.entitlement_missing` SHALL be incremented with label `tenantId`

### Requirement: Grandfather-vs-immediate-cutoff product decision recorded
The product team SHALL record a durable decision specifying whether entitlement revocation triggers immediate cutoff (option A) or a defined grace window (option B). Until a different decision is recorded, option A (immediate cutoff after `FeatureGateCache` TTL) SHALL be the enforced default.

#### Scenario: Immediate cutoff after plan feature revocation (option A — default)
- **GIVEN** `PlanFeature.PlatformLlm` is revoked for a tenant at plan-change time
- **WHEN** the `FeatureGateCache` TTL expires (or cache is invalidated)
- **THEN** the next classify call for that tenant SHALL degrade to empty with no grace window
- **THEN** the `TenantLlmConfig.AiSource` row value SHALL NOT be mutated by the revocation (re-entitlement restores service without reconfiguration)

#### Scenario: Re-entitled tenant automatically recovers
- **GIVEN** a tenant that was previously degraded due to missing entitlement
- **WHEN** `PlanFeature.PlatformLlm` is re-enabled and `FeatureGateCache` reflects it
- **THEN** the resolver SHALL resume returning the platform `ILlmProvider` with no admin reconfiguration required

### Requirement: No double-billing after entitlement revocation
After `PlanFeature.PlatformLlm` is revoked for a `PlatformManaged` tenant, `IMeteringService.RecordUsageAsync` MUST NOT be called with `UsageType.AiAnalysis` for classify calls that degrade due to the missing entitlement. Billing attribution to non-entitled tenants SHALL be prevented.

#### Scenario: No usage record on degraded classify
- **GIVEN** a `PlatformManaged` tenant with no current `PlanFeature.PlatformLlm` entitlement
- **WHEN** a classify call degrades to empty due to the entitlement re-check
- **THEN** `IMeteringService.RecordUsageAsync` SHALL NOT be called with `UsageType.AiAnalysis` for that call

#### Scenario: Usage record still emitted for entitled PlatformManaged classify
- **GIVEN** a `PlatformManaged` tenant with current `PlanFeature.PlatformLlm` entitlement
- **WHEN** a classify call succeeds and returns a non-empty suggestion
- **THEN** `IMeteringService.RecordUsageAsync` SHALL be called with `UsageType.AiAnalysis` and `quantity = totalTokens`

## Architectural Risk

**Level:** LOW

**Affected:** `Verbara.Platform.Llm` (`DefaultLlmProviderResolver`), `Verbara.Platform.Api` (`ConversationEndpoints` classify path), Billing (`UsageRecord` accuracy for downgraded tenants). No SDK/Pro changes. No Web changes (disabled toggle already in place via C5 fix).

**Mitigation:** `IFeatureGateService` is already present in the classify seam; the entitlement check is a single `IsFeatureEnabled` call against the in-memory `FeatureGateCache` (no new I/O). The degrade path is the existing `null`-return / `EmptySuggestion` — no new failure mode. The AOT constraint is unaffected: no new reflection, no new DTOs beyond what is already in `ApiJsonContext`.
