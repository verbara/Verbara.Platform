# Typification P2c.2 — Platform-managed LLM as a metered, gated, billed service

> Phase **P2c.2** of [ADR-0029](../decisions/0029-typification-cascading-conditional-ai-module.md). Builds on **P2c.1** (per-tenant **BYO** LLM config — `Verbara.Platform.Llm` resolver + typed providers + `tenant_llm_config` migration 009) and **P2b** (calibration-gated AutoFill + per-tenant daily token budget). **Cross-repo: Platform (API) + minimal Web.** **No Pro SDK release required** (the new gate is a `Verbara.Platform.Core` `PlanFeature`; the Pro license flag `TypificationAi` already exists).
>
> **Scope (núcleo, decided 2026-06-23):** let an **entitled** tenant switch its Typification AI provider from BYO to a **Verbara-operated** LLM, **metered in AI Credits**, **gated** by plan entitlement, and **capped** by a monthly credit allowance enforced through the existing Billing package. **AI stays strictly opt-in** (the deterministic P0/P1 floor never needs an LLM).

## 1. Context, scope, non-goals

### Problem
P2c.1 made each tenant bring its **own** LLM key (BYO). Verbara cannot sell a managed AI experience: there is no way for a tenant to consume **Verbara's** LLM, no access-gating of that consumption, and no metering/billing of it. The `ConversationEndpoints.cs:317` comment ("Verbara's OpenAI-compatible provider does populate usage") already anticipates a platform-operated endpoint; the Billing package already declares an unused `UsageType.AiAnalysis` slot. P2c.2 connects these.

### Scope (this spec — "núcleo")
1. **Platform-managed provider** — a tenant opts its `tenant_llm_config` from `Byo` to `PlatformManaged`; the resolver serves Verbara's operator key/model through the existing provider seam.
2. **Gating** — a new per-tenant `PlanFeature.PlatformLlm` entitlement (stacked under the existing Pro license `TypificationAi` and the RBAC `typification:ai:configure`).
3. **Metering in tokens (commercialized as AI Credits)** — every platform-managed classify records a durable `UsageRecord` (`UsageType.AiAnalysis`, `UsageUnit.Tokens`, quantity = `TotalTokens`) with `inputTokens`/`outputTokens` in metadata; **AI Credits are derived by aggregation** (Σtokens ÷ `CreditTokenRatio`) at the quota/usage/invoice layer — no lossy per-call rounding.
4. **Monthly credit allowance / quota** — enforced via the Billing `IQuotaEnforcementService` (durable, cross-replica), `QuotaAction` = Warn / SoftBlock (degrade) / HardBlock (402).
5. **Surfaces** — admin opt-in toggle on `/admin/ai/llm-config`, a tenant-facing `GET /admin/ai/credits` usage read, minimal Web (radio + usage readout, EN/ES/PT).
6. **Invoicing** — reuses the existing `IInvoiceGenerationService` (rates `AiAnalysis` credits against a `RateCard`); **no new invoicing code**.

### Non-goals (deferred to a later phase)
- Full Web usage dashboard / period reports.
- **Automatic** overage billing + dunning automation (overage is recorded + invoiceable, but no auto-dunning here).
- **Active** input/output split pricing (the in/out token data is captured in metadata now, so this is enabled later with no migration/backfill).
- Prepaid **credit top-ups / a credit ledger** (Approach C — revisit if prepaid purchase becomes a requirement).

### Load-bearing principle
**AI strictly opt-in.** No platform config / not entitled / disabled / SoftBlock-over-quota → the existing **empty-suggestion** degrade (`AiMode.Off` floor). P2c.2 never makes AI mandatory and never surfaces an error to the agent for any of these states.

## 2. Decisions (Q1–Q5, approved 2026-06-23)

| # | Decision |
|---|----------|
| Q1 | **Per-tenant entitlement** (new `PlanFeature`) + metered/billed. Layered gate: License(operator) → PlanFeature(tenant) → RBAC(user) → opt-in. |
| Q2 | **Technical unit = tokens** (billable quantity = `TotalTokens`); **`inputTokens`/`outputTokens` stored in `UsageRecord.Metadata`** for forward-compat (option 2 = in/out pricing later, no migration). **Commercial unit = AI Credits.** |
| Q3 | **1 AI Credit = N tokens** (fixed bundle; `CreditTokenRatio` configurable in `RateCard`/options). Consumption derived deterministically from `TotalTokens`. |
| Q4 | **Monthly credit allowance via Billing `IQuotaEnforcementService`** (reads Postgres `UsageRecord`s → exact across AOT replicas). `QuotaAction`: Warn / SoftBlock→degrade-to-empty / HardBlock→402 + overage invoiceable. |
| Q5 | **Núcleo scope** (provider+gate+opt-in+metering+credit-conversion+monthly allowance+tenant usage endpoint+admin toggle+minimal Web). Invoicing reuses existing service. |
| Arch | **Approach A — extend existing seams in place** (one migration, minimal new surface; clean path to a ledger later). |

## 3. Architecture

Four existing seams extended in place; no new module:

1. **`Verbara.Platform.Llm`** — `AiSource` discriminator + a 3rd resolution branch building a provider from host-bound `PlatformLlmOptions`.
2. **`Verbara.Platform.Typification`** — credit metering hook in the classify path; reuse the existing P2b budget recording as orthogonal soft-budget/observability.
3. **`Verbara.Platform.Billing`** — `UsageUnit.Tokens`, record `AiAnalysis` usage (in tokens), `TenantQuota.AiCreditsMonthly`, `IQuotaEnforcementService` AI branch, a `RateCard` `AiAnalysis` entry (token→credit conversion + per-credit price).
4. **`Verbara.Platform.Api`** — admin opt-in field + tenant credit-usage endpoint + DTOs in `ApiJsonContext`.

### 3.1 Provider resolution (`Verbara.Platform.Llm`)
- `TenantLlmConfig` gains `AiSource AiSource { get; init; }` — enum `Byo`=0 (default) · `PlatformManaged`=1.
- **`PlatformLlmOptions`** (host-bound `IOptions`): `Enabled`, `BaseUrl`, `ApiKey` (from secret/env — never persisted per-tenant, never serialized), `Model`, `CreditTokenRatio` (default 1000). Registered in `AddPlatformLlm` (`ServiceCollectionExtensions.cs:37`).
- `DefaultLlmProviderResolver.ResolveAsync` (`DefaultLlmProviderResolver.cs:17`): when `config.AiSource == PlatformManaged` **and** `PlatformLlmOptions.Enabled`, build `LlmEffectiveOptions.FromProviderOptions(platformOptions)` (the seam already used for the global host path — `LlmEffectiveOptions.cs:35`) → existing provider `switch`. BYO key path unchanged.
- `ComputeFingerprint` (`DefaultLlmProviderResolver.cs:153`) extended to include `AiSource` + a platform-options version token (so a key/model rotation evicts the cache).
- Resolver returns `null` (→ `AiMode.Off`) when platform is disabled, not entitled, or SoftBlock-over-quota (the quota check is upstream in the classify path; see §3.4).

### 3.2 Gating + opt-in (`Core` + `Api`)
- `PlanFeature.PlatformLlm` added to `Core/PlanFeature.cs:3` + `FeatureGateCache`. Platform-type tenants bypass (existing behavior).
- Opt-in via the existing admin group `TenantLlmConfigEndpoints.cs:28` (`/admin/ai/llm-config`, `AdminOnly` + `RequireOperationalTenant`):
  - `PUT ""` accepts `aiSource`. Transitioning **to** `PlatformManaged` requires: `PlanFeature.PlatformLlm` (checked inline via `IFeatureGateService.IsFeatureEnabled`) **and** RBAC `typification:ai:configure` (existing `EnsureConfigurePermissionAsync`, `:305`). Setting `PlatformManaged` ignores/clears BYO key fields; setting `Byo` requires a key as today.
  - `GET ""` response gains `aiSource` + `platformLlmAvailable` (entitlement bool). The operator key is **never** returned (only BYO `KeySet`/`KeyLast4` as today).
- Runtime gates stack on the classify route unchanged: Pro license `TypificationAi` (`ConversationEndpoints.cs:45`) + `llm` rate-limit (30/min, `:46`).

### 3.3 Metering + credit conversion (`Typification` + `Billing`)
- **`UsageUnit.Tokens`** added (`UsageUnit.cs:6`). Tokens is the technical/stored unit; **AI Credits is a derived commercial unit** (see credit conversion below).
- **`ITypificationCreditMeter`** (new; `Verbara.Platform.Typification.Ai` or a thin Api service). On a **successful platform-managed** classify only (BYO is **never** metered to Billing — the tenant pays its own provider), invoked in the classify path next to the existing budget recording (`ConversationEndpoints.cs:318`):
  - `IMeteringService.RecordUsageAsync(tenantId, UsageType.AiAnalysis, quantity: totalTokens, unit: UsageUnit.Tokens, channel: null, referenceId: conversationId, metadata: { "inputTokens", "outputTokens", "model" }, ct)`.
  - **Credit conversion is applied on AGGREGATION, never per-call:** `credits = Σ(tokens over period) ÷ CreditTokenRatio` (ratio from `PlatformLlmOptions`/`RateCard`). This avoids per-call `ceil` over-billing (a 100-token call must not round up to a full credit).
- The P2b `ITypificationTokenBudget` recording stays (orthogonal per-schema **daily soft** budget + observability); the **billable/quota** source of truth is the durable `UsageRecord`s.
- **Invoicing:** a `RateCard` `RateEntry` for `AiAnalysis` (price per credit; `IncludedQuantity` = the plan's bundled credits; tiered overage). Existing `DefaultInvoiceGenerationService` rates it — no new code.

### 3.4 Quota / allowance (`Billing`)
- `TenantQuota` gains `AiCreditsMonthly` (nullable; `null` = unlimited / pay-as-you-go) — the admin-facing allowance is in **credits**. `DefaultQuotaEnforcementService` enforces it by comparing Σtokens (this period, from `UsageRecord`s) against the token-equivalent threshold `AiCreditsMonthly × CreditTokenRatio`. `GetLimitForType` (`:67`) returns that token threshold for `UsageType.AiAnalysis`.
- **Pre-classify** (platform-managed only), before `ClassifyAsync`: `IQuotaEnforcementService.CheckQuotaAsync(tenant, AiAnalysis, projected≈1)` → `QuotaCheckResult(Allowed, Reason, UsagePercent)`. Mapped to `QuotaAction`:
  - **Warn** (e.g. `UsagePercent ≥ 80`): proceed; emit metric + audit `typification.ai.credits.warn`.
  - **SoftBlock** (at/over limit; **default**): degrade-to-empty suggestion (AI opt-in floor); audit `typification.ai.credits.softblock`.
  - **HardBlock** (per-tenant opt-in): **HTTP 402** on the AI route; audit `typification.ai.credits.hardblock`.
- Accuracy: the service sums `UsageRecord`s from Postgres → correct across AOT replicas (closes the in-memory single-instance gap of `ITypificationTokenBudget`).
- Overage (no auto-dunning here): if over allowance and action ≠ HardBlock, usage is still recorded and invoiced as overage by the rate card.

### 3.5 Admin / usage surface (`Api` + Web)
- **`GET /admin/ai/credits`** (tenant-facing, `AdminOnly` + `RequireOperationalTenant`, RBAC `typification:ai:configure`): current-period `allowanceCredits` (nullable), `consumedCredits` (= Σtokens this period ÷ `CreditTokenRatio`), `remainingCredits`, `usagePercent`, `periodEnd`, `actionOnExhaustion`. New DTOs registered in `ApiJsonContext.cs`.
- `TenantLlmConfigResponse` gains `aiSource` + `platformLlmAvailable`; `UpsertLlmConfigRequest` gains `aiSource`.
- **Web (minimal):** the existing LLM-config admin page gains a "Use Verbara-managed AI (credits)" radio (vs BYO) + a credit-usage readout (calls `GET /admin/ai/credits`); EN-US/ES-419/PT-BR i18n keys (CI parity enforced).

## 4. Data model — migration **010_platform_llm_ai_source**
- `tenant_llm_config` + `ai_source SMALLINT NOT NULL DEFAULT 0` (0=Byo, 1=PlatformManaged). No key columns needed for platform; BYO key columns stay nullable.
- `tenant_quotas` + `ai_credits_monthly BIGINT NULL` (null = unlimited).
- Stores follow the `Verbara.Sdk.Data.Npgsql` pattern (NpgsqlExecutor, hand-written `Row.Map`, explicit `NpgsqlParameter`, **no Dapper**); `PostgresTenantLlmConfigStore` + `PostgresTenantQuotaStore` updated. Migration runner: `DatabaseMigrationService`.

## 5. Request flow (AI-suggestion classify)
1. Request → license gate (`TypificationAi`) → rate-limit (`llm`, 30/min) → endpoint.
2. Resolve `TenantLlmConfig`. If `AiSource == PlatformManaged`: re-check `PlanFeature.PlatformLlm`; resolver builds the platform provider.
3. **Pre-check quota** (`CheckQuotaAsync`): SoftBlock → empty suggestion · HardBlock → 402 · else proceed.
4. `ClassifyAsync` → platform LLM → `LlmResponse.Usage`.
5. **Post:** `ITypificationCreditMeter` records `UsageRecord` (credits + in/out metadata); P2b token budget recorded.
6. **Periodic:** `IInvoiceGenerationService` rates `AiAnalysis` credits against the `RateCard`.

## 6. Fail-closed invariants
- AI strictly opt-in — every "can't serve platform AI" state degrades to the empty suggestion, **never** an error to the agent (HardBlock 402 is the single, tenant-opted-in exception).
- The operator key is never serialized, returned, or logged (host-side only).
- BYO unaffected: its key path, masking, and `/test` are unchanged; **BYO is never metered to Billing**.
- Provider runtime failure → the existing fail-closed degrade.

## 7. Testing (xUnit, NSubstitute; `Method_ShouldExpected_WhenCondition`)
- **Resolver:** platform branch builds from `PlatformLlmOptions`; returns null when disabled/not-entitled; fingerprint changes with `AiSource` + options version.
- **Metering:** `UsageRecord` recorded in tokens (quantity = `TotalTokens`, `UsageUnit.Tokens`) + `inputTokens`/`outputTokens` metadata; **credit conversion is aggregate-only** (Σtokens ÷ ratio — assert no per-call rounding); **BYO path records nothing** to Billing.
- **Quota:** Warn / SoftBlock→degrade / HardBlock→402; cross-`UsageRecord` summation; `null` limit = unlimited.
- **Gating:** opt-in to `PlatformManaged` rejected without `PlanFeature.PlatformLlm`; allowed with it.
- **Endpoints:** `GET /admin/ai/credits` shape; `aiSource` round-trip on PUT/GET; operator key never leaked.
- **AOT:** all new DTOs in `ApiJsonContext`; no reflection; AOT publish gate clean.

## 8. Open questions (resolved) / tunables for the plan
- Default `CreditTokenRatio` (proposed 1000 tokens/credit) and the Warn threshold (proposed 80%) are RateCard/config tunables — finalize in the plan, not load-bearing for the architecture.
- `ITypificationCreditMeter` placement (Typification vs a thin Api service) — decide in the plan; the call site (`ConversationEndpoints` classify path) is fixed.

## 9. Cross-repo & sequencing
- **Platform:** Llm + Typification + Billing + Api + migration 010 (one PR or risk-batched per FCM).
- **Web:** the config-page radio + credit readout + i18n (separate PR).
- **No Pro SDK release** (`PlanFeature` is `Platform.Core`; the Pro `TypificationAi` license flag already ships in 2.8.0-pro).
