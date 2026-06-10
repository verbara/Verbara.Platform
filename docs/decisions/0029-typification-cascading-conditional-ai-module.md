# ADR-0029: Typification — cascading, conditional, AI-assisted disposition module (clean-break rename)

- **Status:** Accepted — **P0 SHIPPED 2026-06-07** (Pro #2 → `Verbara.Sdk.Pro` v2.7.5-pro · Platform #48 · Web #82; all merged to `main`). P1–P4 pending.
- **Date:** 2026-06-07
- **Deciders:** Verbara maintainer (Harol A. Reina H.)
- **Related:**
  - Spec: [`docs/specs/2026-06-07-typification-cascading-conditional-ai.md`](../specs/2026-06-07-typification-cascading-conditional-ai.md)
  - Supersedes the flat `Disposition` domain in [`src/Verbara.Platform.Conversations/Disposition.cs`](../../src/Verbara.Platform.Conversations/Disposition.cs) + [`WrapUpRecord.cs`](../../src/Verbara.Platform.Conversations/WrapUpRecord.cs) + [`DispositionEndpoints.cs`](../../src/Verbara.Platform.Api/Endpoints/DispositionEndpoints.cs)
  - Reuses: Flow engine `http_request` node (data-dips), the Pro AI stack (`CallAnalyticsEngine`, `AgentAssistSession`), `ChannelType`/queue/campaign context at wrap-up, `TagSource.AutoAi`
  - Related ADRs: [`0020`](0020-csat-brownfield-survey-domain-extension.md) (brownfield-first domain extension discipline), [`0022`](0022-platform-api-aot-shipping-path.md) (Native AOT constraint)

## Context

### What exists today

Verbara ships a **flat, dual-layer disposition model**:

- **Platform tenant-scoped** — [`Disposition`](../../src/Verbara.Platform.Conversations/Disposition.cs) (`DispositionId`, `TenantId`, `Name`, `Category ∈ {Success, Failure, FollowUp}`, `IsActive`). Applied at wrap-up to **any** conversation (voice inbound/outbound + all digital channels) via [`WrapUpRecord`](../../src/Verbara.Platform.Conversations/WrapUpRecord.cs) (a single `DispositionId` + free-text `Notes`). Tables `dispositions` + `wrap_up_records` in [`001_InitialSchema.sql:444-456`](../../src/Verbara.Platform.Storage.Postgres/Migrations/001_InitialSchema.sql).
- **Pro Dialer campaign-scoped** — `Verbara.Sdk.Pro.Dialer.Models.DispositionCode` (`Code`, `Label`, `Category ∈ {Success, Failure, Retry, SystemResult}`, `TriggerRetry`/`RetryDelayMinutes`, `TriggerCallback`, `SortOrder`) for outbound campaigns, applied to `call_attempts`, driving retry/callback automation.

Both are **flat single-select lists**. The agent UI ([`wrap-up-dialog.tsx`](https://github.com/verbara/Verbara.Platform.Web/blob/main/src/agent/conversation/wrap-up-dialog.tsx)) is one select + a notes textarea + a conditionally-revealed callback date/phone (the **only** dependent-field behavior, gated by `triggerCallback`). Admin CRUD is `/admin/dispositions` (GET/POST/DELETE — **no update**) gated by `LicenseFeature.Dialer`.

**What is NOT possible today:** hierarchy (category→subcategory→reason), conditional/dependent fields ("respuestas entrelazadas"), structured fields beyond a single code + free notes, per-queue / per-channel / per-direction configuration, and any AI auto-disposition — even though all the AI building blocks already exist and are unwired (see below).

### What the market does (research 2026-06-07)

Cascading + conditional + structured typification is a **standard expected capability**, but vendors deliver it in **two layers**: flat disposition codes (table stakes) **+ a separate layer** for the cascade/conditionality — scripts (Genesys), Step-by-Step Guides (Amazon Connect), conditional + nested ticket fields (Zendesk), dependent picklists / Dynamic Forms (Salesforce). **Only Talkdesk and Google CCAI** nest the hierarchy *inside the disposition object itself* (CCAI up to 5 levels). CRM-anchored players (Zendesk/Salesforce/Connect) offer the richest conditional + external-lookup capture. **AI auto-disposition + summary** (Copilot/Einstein) is now a 2025-2026 baseline expectation, agent-confirms.

**The deeper insight:** the best systems do not build an isolated wrap-up form — they thread **one "reason-for-contact" taxonomy** end-to-end: IVR/bot menu capture → attached data → routing → screen-pop → form pre-fill → cascading disposition → automation → analytics. The same tree the customer navigates becomes the routing key, the screen-pop context, the form seed, and the disposition pre-selection.

### What Verbara already has (~75% of the infrastructure)

- **Flow engine (real DAG)** — [`FlowDefinition`/`FlowNode`/`FlowExecutionEngine`](../../src/Verbara.Platform.Flows/) with nodes `collect_input` (capture+regex), `condition` (branch), `set_variable`, **`http_request`** (external data-dip with `{{var}}` templating + circuit-breaker/retry), `enqueue`, plus AI nodes `ai_classify`/`ai_generate`/`knowledge_search`. Visual XY-Flow designer in Web.
- **Wrap-up context** — `Conversation.Metadata` (attached data), `Channel` (`ChannelType` 0..10), direction, queue and campaign all available at wrap-up. `Queue.WrapUpConfig` is an extensible per-queue hook.
- **Integration** — outbound webhooks (signature + circuit-breaker) + resilient HTTP client.
- **AI stack (Pro + Platform)** — real-time dual-stream STT, real-time + post-call sentiment, Agent Assist suggestions (`AgentSuggestion.Metadata` + `Source = AutoAi`), `CallAnalyticsEngine` whose **`CallSummary.DispositionCode` field already exists for auto-disposition**, topic classifier, PII/entity redaction (document/account/card…), RAG/knowledge search, `ai_classify`/`ai_generate` (JSON-capable). `TagSource.AutoAi` already exists.

### What is genuinely missing (~25% — the new work)

1. A **hierarchical + conditional** typification domain (`parent_id`, structured fields, `visibleWhen`).
2. A **schema-driven dynamic form renderer** in Web (today every form is hand-coded RHF/Zod).
3. **Binding** of typification per queue / campaign / channel / direction (the context exists; the config model does not).
4. **End-to-end AI auto-disposition orchestration** (the pieces exist but are not wired to the wrap-up form).
5. An **admin form designer**.

### Constraints in force

Native AOT (no reflection; source-gen `[JsonSerializable]`), `TreatWarningsAsErrors`, raw Npgsql, API-first dependency chain (`Sdk → Sdk.Pro → Platform ← Platform.Web`), Conventional Commits. **No production customers yet** — a one-time window to take a clean break and pay down debt.

## Decision

Build a **first-class `Typification` module** that supersedes the flat `Disposition` domain, designed as the **hybrid** of the option space (native hierarchical + conditional model with a dynamic form engine, that *reuses* the flow-engine data-dip pattern, the AI stack, and the existing channel/queue/campaign context). The same taxonomy is **shared end-to-end** (IVR capture ↔ wrap-up). Confirmed sub-decisions:

- **D1 — Architecture = Hybrid (Option C).** A dedicated `Verbara.Platform.Typification` domain project owns the hierarchical + conditional schema and is the single source of truth for typification (clean reporting/UX). It **reuses** (a) the `http_request` resilience pattern for external **data-dips**, (b) the Pro AI stack (`CallAnalyticsEngine` → `CallSummary.DispositionCode`, `ai_classify`, `AgentAssistSession`) for **auto-suggest/pre-fill**, and (c) `ChannelType`/queue/campaign context for **scoping**. We do **not** model the wrap-up form as a raw flow (rejected — see Alternatives).

- **D2 — One shared "reason-for-contact" taxonomy, threaded end-to-end.** The same node tree feeds IVR/bot capture (`collect_input` writes the selected path into `Conversation.Metadata.reasonPath`), routing, screen-pop, agent pre-fill, the cascading disposition (pre-selected, not re-classified), automation and analytics roll-ups.

- **D3 — Licensing = new `LicenseFeature.AdvancedTypification`,** independent of `LicenseFeature.Dialer` (cascading typification is needed by inbound/digital tenants who have no outbound dialer). The AI sub-capabilities additionally require the existing Pro AI license features. Flat single-select stays available in the base; cascade + conditional fields + AI + data-dips are gated by `AdvancedTypification`.

- **D4 — Clean-break rename + cleanup (no back-compat, pre-launch).** Rename `Disposition` → the `Typification` module and **remove** the legacy flat shape rather than carry shims. Because there are no customers and no production data, this is the moment to **consolidate the Postgres migration set into a clean baseline** and remove dead/duplicated schema. **Confirmed 2026-06-07: full baseline squash** — collapse `001..034` into a single fresh `001_Baseline.sql` (see Consequences/Open scope). The Pro Dialer `DispositionCode` stays in Pro (dependency direction) and the Api keeps bridging an outbound campaign's typification **leaf** to the Pro dialer code at wrap-up.

- **D5 — Tree depth = configurable per tenant, default 5, hard max 8.** Depth is derived from the parent chain and validated on publish.

- **D6 — New `Verbara.Platform.Typification` project** (mirrors the `Verbara.Platform.Surveys` precedent for a clean module boundary). Admin endpoints `/admin/typification/*`; runtime `GET /conversations/{id}/typification-form` + `POST /conversations/{id}/typify`. All DTOs source-gen-registered in `ApiJsonContext`; schema persisted as JSONB deserialized into typed records (dynamic data, static types → AOT-safe).

### Phasing (each phase independently shippable, own spec→plan)

| Phase | Title | Repos | Delivers |
|---|---|---|---|
| **P0** | Cascading + conditional core (manual) | Platform + Web | Hierarchical + conditional schema, clean-break migration, CRUD, binding (queue/campaign/channel/direction + tenant default), `<DynamicTypificationForm>` in wrap-up, basic designer. **Closes "formularios complejos con respuestas entrelazadas".** |
| **P1** | Shared taxonomy capture | Platform + Web | `collect_input` writes `reasonPath`; wrap-up pre-selects; screen-pop/pre-fill from captured data. |
| **P2** | AI auto-disposition | Platform + Web + Pro (wiring) | `CallAnalyticsEngine`/`ai_classify`/entities → suggest + pre-fill node path & fields; confidence threshold + sentiment gating; `TagSource.AutoAi`; real-time Agent-Assist suggestion for voice. |
| **P3** | External data-dips | Platform + Web | Field-level external lookups (reuse `http_request`, secure variant for PII), dynamic select options, visibility gating, per-tenant connectors. |
| **P4** | Analytics + designer polish | Platform + Web | Roll-ups by taxonomy, drag-drop builder, AND/OR conditions, import/export. |

P0 ships the headline capability; its model is designed from day one to host P1–P4 (taxonomy already "shareable", fields already carry data-dip/AI prefill refs, submission already records AI fields).

## Alternatives considered

| Option | Why rejected |
|---|---|
| **B — Reuse the Flow engine wholesale as the typification engine** (model each form as a flow: `condition` + `collect_input` + `http_request`). | Maximum reuse, but a flow is a continuous DAG *walk*, not a form. Admins would author wrap-up forms on a flow canvas (powerful but heavy), agents get a step-by-step path instead of a coherent form, and reporting roll-ups by taxonomy are awkward (no first-class leaf/node entity). We still reuse the engine's *data-dip pattern* (D1) without inheriting its UX/reporting model. |
| **A — Pure hierarchical disposition, no flow/AI reuse** (Talkdesk model only). | Misses the user-selected end-to-end + AI + data-dip scope; would re-implement HTTP resilience and ignore the existing AI stack. |
| **Keep flat + bolt on hierarchy only.** | Does not deliver conditional "respuestas entrelazadas", AI, or data-dips — the core of the request. |
| **Keep the `Disposition` name and extend in place.** | Considered and explicitly overruled by the maintainer: pre-launch with no customers, a clean `Typification` module + migration consolidation removes the flat-era debt now rather than carrying a confusing half-renamed surface forever. |
| **Gate behind `LicenseFeature.Dialer`.** | Would lock cascading typification to outbound-dialer tenants; inbound/digital (healthcare, multi-product support) are the primary beneficiaries → new independent `AdvancedTypification` feature instead. |

## Consequences

### Positive

- Delivers a capability **ahead of the flat-code majority** and on par with the richest CRM-anchored vendors, **inside one coherent module** (collapses the script-vs-disposition split most vendors force on admins).
- **One taxonomy reused end-to-end** → no redundant re-classification at wrap-up; routing, screen-pop, pre-fill and reporting all line up.
- **Maximal reuse** of proven infra (HTTP resilience, AI stack, channel/queue context, RHF/Zod) → ~75% leverage, ~25% net-new.
- **Clean break now** removes flat-era debt (no shims, consolidated migrations) while it is free to do so.
- **AOT-safe** — dynamic at the data level, static at the type level; all DTOs source-gen.

### Negative / risks

- **Largest single feature since the W-series** — must be phased; P0 alone is a substantial cross-repo train.
- **Breaking rename** touches every disposition reference (endpoints, stores, ApiJsonContext, CLI, tests, Web admin + agent surfaces, the outbound-campaign bridge). Acceptable only because there are no customers; must be done atomically per repo.
- **Dynamic form renderer** is new surface area (field-type registry, condition evaluator) — a focused, well-bounded unit, but net-new and security-sensitive (validation must be server-enforced, never client-only).
- **Schema-as-JSONB** trades query-on-definition for AOT simplicity; submissions are normalized + indexed so reporting stays fast.

### Open scope (locked during planning, before P0 kickoff)

- **Migration consolidation extent** — **RESOLVED 2026-06-07 → full baseline squash (b).** Collapse `001..034` into a single fresh `001_Baseline.sql` (pre-launch, no data), executed as a dedicated companion task verified against a clean DB **and** the Postgres test fixtures *before* P0 domain work lands.
- **Pro Dialer `DispositionCode` alignment** — keep as-is + bridge for P0; optional internal rename in Pro is a later cleanup.

## Implementation notes

- New project `src/Verbara.Platform.Typification/` (domain) consumed by `Verbara.Platform.Api`; storage in `Storage.Postgres` + `Storage.InMemory` mirroring the disposition stores being removed.
- **Licensing mechanics (who defines / validates / enforces):** the `LicenseFeature` enum is **defined in Pro** ([`Verbara.Sdk.Pro.Licensing/LicenseFeature.cs`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/src/Verbara.Sdk.Pro.Licensing/LicenseFeature.cs), a `[Flags]` enum: `Cluster|Dialer|EventStore|Analytics|MultiTenant|Routing|AgentAssist|CallAnalytics|Realtime`) — add `AdvancedTypification = 1 << 9` there + extend the tier-mapping. The signed `.lic` is **issued** by the verbara-website Worker (`developer-license`) and **cryptographically validated** (signature / expiry / `AuthorizedImageDigests` image-binding) by Pro's `LicenseValidator` inside the AOT image. **Runtime enforcement lives in Platform.Api**: `RequireLicenseFeature(LicenseFeature.AdvancedTypification)` tags the route group ([`LicenseFeatureMetadata.cs`](../../src/Verbara.Platform.Api/Middleware/LicenseFeatureMetadata.cs)); [`LicenseGateMiddleware`](../../src/Verbara.Platform.Api/Middleware/LicenseGateMiddleware.cs) checks `ILicenseStatus.LicensedFeatures.HasFlag(...)` and returns **HTTP 402** (with `trial_url`/`upgrade_url`/`contact_sales_url`) when absent. P0 needs only the flag (Platform already references `Pro.Licensing`); P2 AI additionally requires the existing `CallAnalytics`/`AgentAssist` features **and** the Pro runtime engines.
- Server-side validation authoritative for required/visibility/typed fields; the Web renderer mirrors it for UX only.
- Reporting indexes on `typification_submissions(tenant_id, leaf_node_id, completed_at DESC)` and on the selected-path for roll-ups.

## Addendum — P1 design refinement (2026-06-08, append-only)

P1 planning (deep analysis grounded in code) **refined** D2's one-line framing without changing the decision. Recorded here; full design in [`docs/specs/2026-06-08-typification-p1-shared-capture.md`](../specs/2026-06-08-typification-p1-shared-capture.md).

- **D2 reframed as an attribute-bag contract.** "Shared taxonomy threaded end-to-end" is realized as a single string→string contract on `Conversation.Metadata` (the market-proven *contact attributes* / *participant data* model). Well-known key `reasonPath` = JSON array of node **`Code`s** (stable across republish; the consumer maps Codes→NodeId against the resolved schema, longest-valid-prefix tolerant), plus arbitrary prefill keys consumed by `TypificationField.PrefillSource{Kind:Metadata}`. One consumer (`ITypificationPrefillResolver`) reads it at `GET /typification-form`. Precedence is by execution order: implicit (routing) stamps first, explicit (bot/flow) overwrites.
- **Capture is broader than `collect_input`.** Four writers: (B1) generic flow-vars→metadata propagation at bot handoff (was discarded — `BotResponse`/`FlowStepResult` gain `FlowMetadata`); (B2) a **new dedicated `collect_reason` flow node** (not `collect_input`) that walks the cascade and writes `reasonPath` by Code; (B3) **implicit digital** via the previously-unused `RouteResult.Metadata` + a new `ReasonHint` rule; (B4) **implicit voice** via a `VERBARA_REASON` channel var set in `StasisInboundConsumer` and read in `VoiceConversationBridge`. Deep analysis found implicit capture (zero customer effort, all channels) is highest-ROI and mostly reuses existing infra.
- **`ReasonHint` (new, in `Verbara.Platform.Typification`)** keyed by `Scope ∈ {Did, Channel, Queue}` → `ReasonPath` (Codes), resolved most-specific-wins (Did→Queue→Channel). Kept separate from `DidRoute` (routing) for SoC. Admin CRUD `/admin/reason-hints` gated `AdvancedTypification`.
- **No Pro/Sdk change in P1** (`AdvancedTypification` already exists). **No new cross-pod SSE event.** Prefill provenance deferred to P4.
- **Latent fix folded in:** the flow designer persisted `node.type` PascalCase while the engine matches snake_case (no publish-time type validation) → designer-built flows would throw at runtime; resolved by a pure bidirectional mapping in the Web `flow-utils.ts` (wire/engine vocabulary = snake_case; PascalCase is a React-Flow render detail).

## Addendum — P2 design refinement (2026-06-09, append-only)

P2 planning (deep analysis grounded in code) **refined** D2/D3's "reuse the Pro AI stack" framing. Full design in [`docs/specs/2026-06-09-typification-p2-ai-auto-disposition.md`](../specs/2026-06-09-typification-p2-ai-auto-disposition.md). Phased **P2a** (provider + core SuggestOnly) → **P2b** (AutoApply + entities + Pro voice enrichment).

- **There is no existing real LLM client anywhere in the ecosystem** (SDK or Pro). The `ILlmProvider` seam (until P1 only a `DisabledLlmProvider` stub) is the FIRST real LLM integration. The Pro AI stack produces flat/voice outputs — `CallAnalytics → CallSummary.DispositionCode` is a single post-call string (voice only); `AgentAssist` is real-time prose; `ai_classify` returns one label — **none produces the cascading root→leaf node path + field entities + confidence that P2 needs across channels.**
- **D2/D3 reframed: the P2 core is a DIRECT, channel-agnostic LLM classifier at wrap-up, not Pro-stack reuse.** A new `ITypificationAiClassifier` reads the conversation transcript (`IMessageStore`) + the resolved schema (leaf Codes/Labels, channel-filtered) and asks the LLM (via the real provider) for structured JSON `{leafCode, confidence, sentiment, fields}`; validates the path against the schema; never throws. The Pro `CallAnalytics`/`AgentAssist` engines become **optional voice enrichment** (sentiment for gating, an already-computed `CallSummary`/`DispositionCode` as a hint) in **P2b**, not the core.
- **New `Verbara.Platform.Llm` project** (resolves a cycle: `Flows`→`Typification` already exists, so `ILlmProvider` cannot be consumed from `Typification` while it lives in `Flows`). `Llm` (no deps) owns `ILlmProvider`/`LlmRequest`/`LlmResponse` + `DisabledLlmProvider` + the open **`OpenAiCompatibleLlmProvider`** (HttpClient + source-gen JSON + `Sdk.Resilience`; covers OpenAI + Azure OpenAI + local Ollama/vLLM via base-URL+model) + `LlmProviderOptions` (deployment-level; per-tenant deferred). Both `Flows` and `Typification` depend on `Llm`. The provider is **open** (it also lights up the `ai_classify`/`ai_generate` flow nodes, dormant since they shipped).
- **Licensing (refines D3): a NEW Pro flag `LicenseFeature.TypificationAi = 1 << 10`.** AI auto-disposition is gated `AdvancedTypification + TypificationAi` — a distinct, separately-priceable premium capability that does NOT force licensing `CallAnalytics` (which the direct classifier doesn't use). The open LLM provider itself is not gated.
- **Async delivery:** a separate `POST /conversations/{id}/typification-suggestion` endpoint (gated) so the wrap-up form stays instant and the AI suggestion streams in. `AiConfig` gating (`SuggestOnly`/`AutoApplyAboveThreshold`, `ConfidenceThreshold`, `SentimentGating`) is enforced server-side; P2a ships `SuggestOnly`. No new cross-pod SSE event.

## References

- Spec: [`docs/specs/2026-06-07-typification-cascading-conditional-ai.md`](../specs/2026-06-07-typification-cascading-conditional-ai.md)
- P1 spec: [`docs/specs/2026-06-08-typification-p1-shared-capture.md`](../specs/2026-06-08-typification-p1-shared-capture.md)
- Current domain being superseded: [`src/Verbara.Platform.Conversations/Disposition.cs`](../../src/Verbara.Platform.Conversations/Disposition.cs), [`WrapUpRecord.cs`](../../src/Verbara.Platform.Conversations/WrapUpRecord.cs), [`DispositionEndpoints.cs`](../../src/Verbara.Platform.Api/Endpoints/DispositionEndpoints.cs)
- Reused engine: [`src/Verbara.Platform.Flows/Nodes/HttpRequestNodeHandler.cs`](../../src/Verbara.Platform.Flows/Nodes/HttpRequestNodeHandler.cs)
- Reused AI stack (Pro): `Verbara.Sdk.Pro.CallAnalytics/Engine/CallAnalyticsEngine.cs`, `Verbara.Sdk.Pro.CallAnalytics/Domain/CallSummary.cs` (`DispositionCode`), `Verbara.Sdk.Pro.AgentAssist/Engine/AgentAssistSession.cs`
- Precedent: [`ADR-0020`](0020-csat-brownfield-survey-domain-extension.md) (brownfield-first, license-feature-additive), [`ADR-0022`](0022-platform-api-aot-shipping-path.md) (AOT)
