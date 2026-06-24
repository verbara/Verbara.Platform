# ADR-0032: Platform-managed LLM entitlement is re-checked at runtime with immediate cutoff

- **Status:** Accepted
- **Date:** 2026-06-24
- **Deciders:** Verbara maintainer (Harol A. Reina H.)
- **Related:**
  - OpenSpec change `openspec/changes/typification-runtime-entitlement-recheck/`
  - Living spec `openspec/specs/typification-platform-llm/spec.md`
  - `src/Verbara.Platform.Api/Endpoints/ConversationEndpoints.cs` (`GetTypificationSuggestion` — the classify path)
  - `src/Verbara.Platform.Api/Services/DefaultFeatureGateService.cs` + `FeatureGateCache.cs` + `Middleware/TenantStatusMiddleware.cs` (per-request feature resolution)
  - `src/Verbara.Platform.Core/PlanFeature.cs` (`PlanFeature.PlatformLlm`)
  - `src/Verbara.Platform.Llm/DefaultLlmProviderResolver.cs` (platform-managed branch, P2c.2)
  - Builds on [ADR-0029 (cascading/conditional/AI typification module)](0029-typification-cascading-conditional-ai-module.md); part of the Typification P2c train (P2c.2 shipped Platform v2.15.0).

## Context

P2c.2 introduced `AiSource.PlatformManaged`: a tenant can opt into Verbara's operator-managed
Typification LLM instead of bringing its own key (BYO), with usage metered as AI Credits
(`ITypificationCreditMeter.RecordAsync`) and gated by a monthly allowance
(`IQuotaEnforcementService`). Access to the feature is governed by the per-tenant entitlement
`PlanFeature.PlatformLlm` (Enterprise plan, or granted as a per-tenant add-on).

As shipped, that entitlement was enforced **only** at the opt-in gate
`PUT /admin/ai/llm-config` (`TenantLlmConfigEndpoints`, `RequirePlanFeature(PlatformLlm)`).
A plan feature can change **outside** the LLM-config flow — a plan downgrade, an add-on expiry,
a partner-customer suspension, or a billing/dunning action. After such a change, the tenant's
stored `TenantLlmConfig.AiSource` still says `PlatformManaged`, so the classify path kept
resolving the operator's LLM and kept recording AI-Credit usage. **A downgraded tenant was being
billed for a feature it was no longer entitled to**, indefinitely, until an admin happened to flip
the config back to BYO. This is an involuntary-billing defect and an entitlement-integrity gap.

Two questions had to be resolved before fixing it:

1. **Policy** — when entitlement is revoked, do we *grandfather* the existing `PlatformManaged`
   config until an admin switches it off (option B, a grace window), or *cut access immediately*
   (option A)?
2. **Enforcement seam** — where does the runtime re-check belong?

### What grounding corrected

Before implementing, the real seams were mapped. Three working assumptions from the original
draft proposal were **wrong** and are corrected here:

- **There is no `FeatureGateCache` TTL.** `FeatureGateCache` is a plain process-wide
  `ConcurrentDictionary<string, ResolvedFeatures>` with no expiry. It is **repopulated on every
  request** by `TenantStatusMiddleware.PopulateCaches()` (which derives features from the tenant's
  *current* plan + add-ons, forcing `Starter` when the tenant is `Degraded`), and is **explicitly
  evicted** (`Remove(tenantId)`) on plan change, partner-customer suspension, and dunning. So a
  revocation is reflected on the **very next request** — there is no TTL lag to wait out.
- **The classify metering API is `ITypificationCreditMeter.RecordAsync`**, not
  `IMeteringService.RecordUsageAsync` (which does not exist in this handler).
- **`DefaultLlmProviderResolver.ResolveAsync` is not on the endpoint's direct call path.** It is
  called one layer down, inside `DefaultTypificationAiClassifier.ClassifyAsync`. The endpoint
  reads `AiSource` itself (via `ITenantLlmConfigStore`) to drive quota + metering, and gating
  decisions are made in the endpoint, not the resolver.

`IFeatureGateService.IsFeatureEnabled(string tenantId, PlanFeature)` is **synchronous**, registered
**singleton**, and reads the per-request-populated cache — a near-free in-memory lookup with no I/O.

A code search confirmed the **only** runtime consumer that resolves *and uses* a platform-managed
provider is the typification classify path (`ConversationEndpoints` → `DefaultTypificationAiClassifier`).
The admin `/test` probe in `TenantLlmConfigEndpoints` is the only other caller and is already
`RequirePlanFeature(PlatformLlm)`-gated. There is no Flows/Bot consumer of platform-managed LLM.

## Decision

**Policy — Option A, immediate cutoff (approved by product owner).** When `PlanFeature.PlatformLlm`
is no longer enabled for a tenant, the platform LLM is cut off immediately — on the next classify
request, with no grace window. The cutoff degrades to empty (the existing `AiMode.Off` / no-config
fail-closed behavior), invisible to the agent.

- The `TenantLlmConfig.AiSource` row value is **NOT mutated** by the revocation. Re-entitling the
  tenant (plan upgrade / add-on restored) restores service on the next request with **no admin
  reconfiguration** — the stored intent (`PlatformManaged`) is honored again as soon as the
  entitlement returns.
- Because `FeatureGateCache` is repopulated per request, "immediate" means **the next request after
  the plan change**, not after any TTL.

**Enforcement seam — the classify endpoint (`GetTypificationSuggestion`), not the resolver.**
The runtime re-check is a single `IFeatureGateService.IsFeatureEnabled(tenantId, PlanFeature.PlatformLlm)`
call placed in `ConversationEndpoints.GetTypificationSuggestion`, **after** the AI-enabled / `AiMode`
gate and **before** the platform-managed quota pre-check. For a `PlatformManaged` tenant missing the
entitlement, the handler:

1. emits a structured audit event `typification.ai.platformllm.entitlement_missing` (via
   `IAuditService.RecordAsync`, severity `warning`, with `tenantId` + `aiSource` metadata),
2. increments the counter `platformllm.degrade.entitlement_missing` on `TypificationAiMetrics`
   (meter `verbara.platform.typification.ai`),
3. returns `EmptySuggestion` (HTTP 200),

thereby skipping the quota pre-check, the classifier call, and the credit meter in one shot. BYO and
no-config tenants are unaffected (the block is guarded by `isPlatformManaged`).

### Why the endpoint and not the resolver

- **It is where the billing decision lives.** Quota pre-check, AI-Credit metering, and the audit
  trail are all in this handler. Co-locating the entitlement gate there guarantees the three
  required outcomes together: no `UsageRecord` on degrade, HTTP **200 not 402** (the resolver
  returning `null` would still let the upstream quota pre-check emit a `402` on a HardBlocked
  tenant), and a typification-namespaced audit event + metric.
- **The audit/metric are typification-specific** (`typification.ai.platformllm.*`). The resolver is
  a generic `Verbara.Platform.Llm` component with no `IAuditService` / `TypificationAiMetrics`;
  emitting them there would be the wrong layer.
- **The resolver is not the only-consumer chokepoint people assume.** Since the sole platform-LLM
  consumer is this endpoint (verified), pushing `IFeatureGateService` down into the `Llm` package
  would add a dependency and force churn across all platform-managed resolver unit tests (which
  would suddenly resolve to `null` without a seeded feature gate) to guard a consumer that does not
  exist. **YAGNI.**
- The per-request cache repopulation already delivers immediate cutoff; no caching subtlety needs to
  be solved in the resolver's fingerprint.

## Consequences

- **Positive:** involuntary billing stops on the next request after revocation; entitlement is now
  enforced continuously, not just at opt-in. Re-entitlement self-heals with no admin action. The
  degrade is silent to the agent (existing empty-suggestion UX). One cheap in-memory lookup per
  classify; no new I/O, no new DTO, no migration, AOT-safe (no reflection).
- **Negative / trade-off:** the gate is enforced at the typification classify endpoint only. This is
  sufficient today (it is the only metered platform-LLM consumer). **If a future feature resolves
  the platform-managed LLM from another path** (e.g. a Flows LLM node, Bot), that path will NOT
  inherit this gate and MUST add its own entitlement check — or the enforcement should be lifted
  into the resolver at that point. This residual is recorded so the next consumer does not assume
  global coverage.
- **Negative (silent-by-design):** the cutoff produces a normal `200` empty suggestion, not an
  error. The audit event + metric are the **only** runtime signal that a tenant is being degraded
  for missing entitlement — operators diagnosing "AI stopped suggesting" must consult them.
- **Neutral:** no change to the resolver, to `PlatformLlmOptions`, to the quota/metering services,
  or to the Web UI (the C5 disabled-toggle already covers the opt-in surface).

## Alternatives considered

- **Option B — grace window (grandfather until admin switches off, or N days).** Rejected: it keeps
  billing a non-entitled tenant during the window (directly contradicting the defect being fixed),
  and requires persisting a revocation timestamp + expiry (new state + a migration) and a scheduled
  re-evaluation — substantial complexity for a forgiveness behavior product did not want.
- **Enforce in `DefaultLlmProviderResolver.ResolveAsync` (return `null` when not entitled).**
  Rejected as the *primary* seam: it does not prevent the upstream quota pre-check from returning
  `402` for a HardBlocked-and-downgraded tenant (violating the "always 200" requirement), and it is
  the wrong layer for the typification-specific audit event + metric. Considered as defense-in-depth
  and dropped (YAGNI — no second consumer exists; would force test churn).
- **Mutate `AiSource` back to BYO/Off on revocation** (a write-side fix at plan-change time).
  Rejected: destroys the tenant's stored intent (they must reconfigure to recover after a
  re-upgrade), couples every plan-change/dunning/suspension write path to LLM-config mutation, and
  still would not cover an entitlement that lapses by add-on expiry rather than an explicit write.
  A stateless per-request read-side check is simpler and self-healing.
