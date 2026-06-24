---
tier: MEDIANO
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

A tenant that opts into `AiSource.PlatformManaged` and later **loses** the `PlanFeature.PlatformLlm`
entitlement continues using — and being billed for — the platform LLM indefinitely. The entitlement
is only checked at the `PUT /admin/ai/llm-config` opt-in gate, not in the runtime classify path
(`DefaultLlmProviderResolver`) or the `ConversationEndpoints` classify route. This creates
involuntary billing exposure for downgraded tenants and a product-policy ambiguity: should the
platform grandfather existing `PlatformManaged` configs until an admin explicitly switches off, or
cut access immediately when the plan feature is revoked?

## What Changes

- Add a `PlanFeature.PlatformLlm` entitlement check inside `DefaultLlmProviderResolver.ResolveAsync`
  (and/or the classify route pre-flight) for tenants whose `AiSource == PlatformManaged`; the
  opt-in PUT gate alone is insufficient because plan features can change outside the LLM config flow.
- Define and record the **grandfather-vs-immediate-cutoff** product decision (billing accuracy,
  UX, and grace-window implications); this change captures the decision as a requirement.
- When the entitlement is absent at runtime, **degrade to empty** (`AiMode.Off` floor) — the same
  fail-closed path as "no config" / BYO failure — so no AI usage is attributed to a non-entitled tenant.
- Emit an observable metric and structured audit event when a `PlatformManaged` tenant is degraded
  due to missing entitlement (needed for billing accuracy and support diagnosis).

## Capabilities

### New Capabilities

- none

### Modified Capabilities

- `typification-platform-llm`: Add runtime entitlement re-check for `AiSource.PlatformManaged`
  tenants in the resolver/classify path (not only at opt-in PUT). Define degrade-to-empty behavior
  and the grandfather-vs-cutoff policy.

## Impact

- **`Verbara.Platform.Llm`** — `DefaultLlmProviderResolver.ResolveAsync`: add
  `IFeatureGateService.IsFeatureEnabled(PlanFeature.PlatformLlm)` call for `PlatformManaged` path.
- **`Verbara.Platform.Api`** — `ConversationEndpoints` classify route: enforce or rely on resolver
  returning `null` (→ degrade) when entitlement is absent.
- **Billing accuracy** — a downgraded tenant that was previously double-billed via the shared
  platform key will stop generating `UsageRecord(AiAnalysis)` entries after this change.
- **`Platform.Web`** — no UI change required; the disabled toggle (C5 fix already in place) covers
  the UX surface. The degrade is silent to the agent (existing empty-suggestion path).
- No cross-repo SDK/Pro changes required; `PlanFeature.PlatformLlm` already exists in
  `Verbara.Platform.Core`.
