# Typification P2c.1 — Per-tenant LLM config (BYO-only)

> Phase **P2c** of [ADR-0029](../decisions/0029-typification-cascading-conditional-ai-module.md) (see the 2026-06-14 P2 re-decomposition addendum). Builds on P2a (`Verbara.Platform.Llm` + `ITypificationAiClassifier`) and P2b (calibration-gated AutoFill + per-tenant token budget). **Cross-repo: Platform + Web.** No Pro dependency. No new Pro license flag.
>
> **This document is P2c.1 (BYO-only).** The platform-managed LLM as a metered/gated/billed service is **P2c.2** (separate spec) — see §1 Non-goals.

## 1. Context, scope, non-goals

### Problem
Today the LLM provider is a **single global config**: `AddPlatformLlm(o => …)` binds one `LlmProviderOptions` (`BaseUrl`/`ApiKey`/`Model`) via `IOptions<>`, a singleton shared by every tenant (`OpenAiCompatibleLlmProvider` reads the static options). For a multi-tenant production product this is wrong on three axes: (a) every tenant shares one key (no isolation, no per-tenant cost control, no BYO), (b) the key is effectively free shared usage, (c) no tenant can pick its own provider/model or keep its data with its own vendor (residency).

### Scope (P2c.1 — BYO-only)
Each tenant brings **its own** LLM provider and credentials ("BYO-key"):
- Per-tenant config stored in Postgres, **API key encrypted at rest** (`IDataProtector`).
- **Pluggable, multi-provider**: `openai_compatible`, `azure_openai`, `anthropic` (extensible).
- Per-tenant **resolution** with caching + invalidation (Architecture A: resolver + typed providers).
- **Fail-closed**: a tenant whose BYO provider fails at runtime gets the existing empty-suggestion degrade — its data is **never** sent to any other provider.
- **Health-check** ("test connection") endpoint + admin UI.
- API CRUD + Web admin form.

### Non-goals (deferred to P2c.2)
The **platform-managed LLM as a service** — i.e. a Verbara-provided key that tenants may use — is out of scope because it is a *commercial* feature, not just config:
- per-tenant **entitlement / access gating** (which tenants may use the platform key),
- **metering → rate card → invoice line** attribution (Billing integration),
- **data-residency / no-train** enforcement flags,
- **per-tenant resilience isolation** (circuit-breaker partitioning).

Because P2c.1 is BYO-only, there is **no platform key to fall back to**; BYO failure ⇒ fail-closed. The current global shared key is **retired** (see §6 transition).

### §0 principle — AI is strictly opt-in (load-bearing)
Deterministic typification is the **floor** and never needs an LLM: P0 (cascading/conditional disposition forms) + P1 (`PrefillResult` hydration from `reasonPath`/captured metadata) run with zero AI. The AI layer is the **optional ceiling**. Three "no-AI" states are **first-class, normal (not error) states**:
1. tenant has **no** `tenant_llm_config`,
2. config exists but `enabled = false`,
3. `AiMode.Off` (per-schema, already the default — `TypificationAiConfig.Mode` defaults to `AiMode.Off`).

In all three the wrap-up runs fully agent-driven with deterministic automation. The endpoint already short-circuits to `EmptySuggestion` (`ConversationEndpoints.cs`: `if (!Enabled || Mode == AiMode.Off) → EmptySuggestion`). **Non-goal: P2c must never make AI mandatory.** The config UI frames "no provider" as a valid mode, not an error.

## 2. Data model — `tenant_llm_config`

One row per tenant (MVP). Type-specific settings live in a **jsonb** column for schema-stable extensibility (a new provider type needs **no DB migration**).

| Column | Type | Notes |
|---|---|---|
| `tenant_id` | `uuid` PK | one config per tenant |
| `provider_type` | `text` | `openai_compatible` \| `azure_openai` \| `anthropic` (extensible) |
| `model` | `text` | e.g. `gpt-4o-mini`, `claude-3-5-haiku-latest` |
| `api_key_encrypted` | `text` | **`IDataProtector`**-protected (purpose `Verbara.Platform.Typification.TenantLlmApiKey.v1`); mirrors `PostgresTenantAuthConfigStore`'s OIDC-secret column |
| `api_key_last4` | `text?` | **non-secret** display hint (last 4 chars), set on upsert; lets `GET` show `••••1234` without decrypting |
| `provider_settings` | `jsonb` | type-specific config (see below) |
| `enabled` | `boolean` | default `false` |
| `created_at` / `updated_at` | `timestamptz` | |

**`provider_settings` shape** — a single typed `ProviderSettings` record with nullable fields, source-gen-serialized (AOT). Extensible: a new provider type adds a nullable field (code only, no migration).
```
ProviderSettings {
  string? BaseUrl;          // openai_compatible, azure (resource endpoint)
  string? AzureDeployment;  // azure_openai
  string? AzureApiVersion;  // azure_openai
  string? AnthropicVersion; // anthropic (anthropic-version header; default if null)
}
```
- jsonb binding: param bound as string with a `::jsonb` SQL cast (the documented exception to the explicit-`NpgsqlDbType` rule in CLAUDE.md); read back as text → deserialized via `TenantLlmJsonContext`.
- Row type: `class { get; init; }` + hand-written `static TenantLlmConfig Map(NpgsqlDataReader)` with name-based getters.
- The **decrypted** key is materialised only inside the resolver at use time; it is never logged and never leaves the server.

## 3. Provider abstraction (pluggable, AOT-safe)

`ILlmProvider` (existing, `CompleteAsync(LlmRequest, ct)`) is unchanged. Three implementations, each a **small, independently testable unit** owning its own wire + auth:

| Type | Impl | Auth | Wire / endpoint |
|---|---|---|---|
| `openai_compatible` | `OpenAiCompatibleLlmProvider` (refactored) | `Authorization: Bearer <key>` | `{BaseUrl}/chat/completions`, reuse `ChatCompletionWire` / `LlmJsonContext` |
| `azure_openai` | `AzureOpenAiLlmProvider` (new) | `api-key: <key>` header | `{BaseUrl}/openai/deployments/{AzureDeployment}/chat/completions?api-version={AzureApiVersion}`, reuse OpenAI wire |
| `anthropic` | `AnthropicLlmProvider` (new) | `x-api-key: <key>` + `anthropic-version` | `https://api.anthropic.com/v1/messages` (or `BaseUrl`), **new** `AnthropicMessagesWire` + new `[JsonSerializable]` context |

- **Refactor:** `OpenAiCompatibleLlmProvider` stops reading `IOptions<LlmProviderOptions>`; it takes **effective options** (base url / key / model) constructed by the resolver.
- **AOT:** Anthropic's request/response DTOs get their own `[JsonSerializable]` source-gen context; no reflection; provider selection is a `switch` over the `provider_type` discriminator.
- **HttpClient:** obtained via `IHttpClientFactory` (named clients per provider type) for socket pooling.

## 4. Resolution + caching + fail-closed (Architecture A)

- **`ITenantLlmConfigStore`** — `GetAsync(tenantId, ct)` → `TenantLlmConfig?`; `UpsertAsync`, `DeleteAsync`. InMemory (dev/test) + Postgres impls.
- **`ILlmProviderResolver.ResolveAsync(tenantId, ct)` → `ILlmProvider?`**:
  - no config / `enabled == false` → returns `null` ⇒ caller path already degrades to `EmptySuggestion` (fail-closed, §0).
  - else: `switch(provider_type)` selects the impl, **decrypts** `api_key_encrypted`, builds the provider with effective options.
  - **cache** keyed by `(tenantId, configHash)` where `configHash` is a stable hash of the persisted config; **invalidated** on `Upsert/Delete` (version bump / evict-by-tenant). Decryption happens at build time, not per-request.
- **Classifier refactor:** `DefaultTypificationAiClassifier` moves from `IOptions<LlmProviderOptions>` (global) to `ILlmProviderResolver` + the request's tenant id. P2b's per-tenant token budget + `llm` rate-limit sit **above** the resolved provider, unchanged.
- **BYO failure semantics:** a runtime failure (auth/5xx/timeout) propagates as the existing "classifier degraded" path → `EmptySuggestion`. The tenant's transcript is **never** re-sent to another provider.

## 5. Web UI (admin)

A per-tenant provider-config page (admin):
- **Provider type** selector → conditional type-specific fields (`BaseUrl`; Azure `deployment` + `api-version`; optional Anthropic version).
- `model` text field; **API key** field — write-only, masked on read (`configured · ••••1234`), never round-tripped.
- `enabled` toggle.
- **"Test connection"** button → calls the health-check endpoint, shows reachable / auth / model / latency or the error.
- Explicit copy: *"Sin proveedor configurado = typificación manual + automatización determinista (un modo válido)."*
- `@base-ui/react` (not Radix, `render` prop). `data-*` selectors. i18n parity EN-US / ES-419 / PT-BR. `ConfirmDeleteDialog` for delete.

## 6. Global-key transition (don't break SMB / dev / demo)

- **Multi-tenant SaaS:** no global runtime key. Each tenant configures its own; unconfigured ⇒ AI off (intentional — closes the free shared key).
- **SMB single-tenant / dev / demo:** an **idempotent startup seed** materialises the existing appsettings/env `LlmProviderOptions` (when set) into the BYO config of the **single operational (customer) tenant** on first run, so the SMB operator's env-var workflow keeps working. The seed runs **only** when (a) an appsettings/env key is configured, **and** (b) exactly one operational tenant exists (single-tenant mode), **and** (c) that tenant has no `tenant_llm_config` row yet — otherwise it is a **no-op** (so multi-tenant SaaS is never auto-seeded). Demo seeds stay under `docker/demo/` only.
- This is a **breaking change for multi-tenant** (tenants lose the shared key until they configure BYO): documented in `CHANGELOG`, and SMB manual `06-canal-voz-sip` (AI/typification section) updated.

## 7. Health-check ("test connection")

- `POST /api/v1/admin/ai/llm-config/test` — runs a **minimal** completion against the **saved** config, or a **draft** config sent in the body (so the operator can test before saving). Returns `{ reachable, authOk, modelOk, latencyMs, error? }`. On-demand (not leader-gated). RBAC `typification:ai:configure`. The key is never logged; draft keys are used in-memory only and not persisted.

## 8. API + RBAC

All under `typification:ai:configure` (existing P2b permission). DTOs are sealed records registered in `ApiJsonContext`; the **API key is never returned**.
- `GET  /api/v1/admin/ai/llm-config` → masked view (`providerType`, `model`, settings, `enabled`, `keySet: bool`, `keyLast4?`, `updatedAt`).
- `PUT  /api/v1/admin/ai/llm-config` → upsert (encrypts the key; omitting the key on update keeps the stored one).
- `DELETE /api/v1/admin/ai/llm-config` → remove the row.
- `POST /api/v1/admin/ai/llm-config/test` → health-check (§7).
- DTO field convention: `id`-style fields so the frontend hooks bind.

## 9. Testing

- **Encryption** round-trip (protect/unprotect via `IDataProtector`); key never present in `GET`/logs.
- **Resolver:** correct impl per `provider_type`; **fail-closed** (`null`) when no config / `enabled=false`; cache hit + **invalidation** on upsert/delete.
- **Per-provider wire/auth:** OpenAI (`Bearer`), Azure (`api-key` header + deployment URL + `api-version`), Anthropic (`x-api-key` + `anthropic-version`, Messages wire).
- **Endpoints:** `PUT` masks the key in the response; `403` without `typification:ai:configure`; `test` happy + failure; update-without-key preserves the stored key.
- **Seed (§6):** idempotent; appsettings → default-tenant row; no-op when unset or row exists.
- **Migration** idempotency.
- Test naming `Method_ShouldExpected_WhenCondition`; xunit + FluentAssertions + NSubstitute. AOT publish stays clean (0 `IL2026`/`IL3050`/`IL207x`).

## 10. Migration

- `009_tenant_llm_config` — additive, idempotent (`CREATE TABLE IF NOT EXISTS`). No change to existing schema. `provider_settings jsonb`.

## 11. Execution shape (FCM)

- **Phase A — Foundation (batch):** migration `009`; `TenantLlmConfig` + `ProviderType` + `ProviderSettings` + `TenantLlmJsonContext`; `ITenantLlmConfigStore` (InMemory + Postgres) with encryption.
- **Phase B — Critical (focused subagents):** the 3 provider impls (wire/auth each) + `ILlmProviderResolver` (selection + decrypt + cache/invalidation). Individually tested.
- **Phase C — Integration (batch):** classifier refactor to the resolver; API endpoints + DTOs (`ApiJsonContext`); DI wiring; startup seed (§6); Web admin page + hook + i18n.

## 12. Open follow-ups (explicitly out of P2c.1)
- **P2c.2:** platform-managed LLM as a metered/gated/billed service (entitlement + Billing rate card + invoice attribution + data-residency/no-train enforcement + per-tenant resilience isolation).
- Multiple configs per tenant (e.g. per-purpose models) — single config per tenant for now.
