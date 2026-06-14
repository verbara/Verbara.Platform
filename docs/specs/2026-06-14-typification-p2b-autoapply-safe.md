# Spec — Typification P2b: AutoApply (safe) + entity prefill (digital/text)

> Phase **P2b** of [ADR-0029](../decisions/0029-typification-cascading-conditional-ai-module.md) (see the **P2b addendum**, which records the P2→P2a/P2b/P2c/P2d split). Builds on **P0** (cascading/conditional), **P1** (shared capture), **P2a** (SuggestOnly classifier + `Verbara.Platform.Llm` provider). **Cross-repo: Platform + Web only** (RBAC/flags live in Platform; **no Pro change**). **Voice enrichment → P2d; per-tenant LLM config → P2c** (both descoped here for hard architectural reasons recorded in the ADR addendum).

## 1. Problem & goal

After P2a the AI **suggests** a disposition at wrap-up and the agent clicks Accept. P2b lets the AI **fill the wrap-up form itself** when confident enough (the agent still commits), and **extract named entities** into form fields — but only on top of the **trust / safety / observability / cost substrate** that makes (near-)autonomous classification safe to ship to a real customer.

The P2a deep audit (2026-06-14, 5-agent sweep) found the inherited P2b scope was "forward-path only" (suggest→apply→prefill) and missing the entire feedback/safety/cost/observability half. **This spec treats that substrate as in-scope and load-bearing, not optional.** The single most important finding: *you cannot safely enable AutoApply without a calibration mechanism that proves the classifier is accurate first* — that mechanism (shadow mode + server-side reconciliation) is the spine of P2b.

**Channel scope: digital/text only.** Voice is architecturally infeasible at wrap-up time (CallAnalytics is post-call async; no `Conversation→SessionId` linkage; `CallSummary.DispositionCode` is dead code; transcript is in-memory and discarded) — it needs Pro prerequisites + a post-call async trigger and becomes P2d.

## 2. Architecture decisions (resolved as principal architect; map to the approved design D-A…D-J)

### D-A — Graduated automation (confidence bands), default human-in-the-loop
Replace the single `ConfidenceThreshold` with two thresholds on `TypificationAiConfig`:
- `SuggestThreshold` (≤) and `AutoApplyThreshold`.
- `confidence < SuggestThreshold` → no suggestion · `[SuggestThreshold, AutoApplyThreshold)` → **suggest banner** (P2a behavior) · `confidence ≥ AutoApplyThreshold` → **auto-fill** the cascade + fields (marked AI-applied, with Undo); **the agent still presses Submit**.
- Keep `AiMode` but extend it: `Off | Shadow | SuggestOnly | AutoFill`. (`AutoApplyAboveThreshold` is renamed `AutoFill` to remove the "applies = commits" ambiguity; migrate the existing enum value.)
- **Autonomous commit** (server writes the disposition with no human) is a *separate* capability — see D-A2 — never the default.

### D-A2 — Autonomous commit (abandoned wrap-up) is a separately-gated tier
Server-side commit of a disposition with no agent action, **only** when ALL hold: wrap-up was **abandoned/timed-out** (agent left without dispositioning); `AutonomousThreshold` (≥ `AutoApplyThreshold`) is met; the binding has `Autonomous` enabled; the actor has the `typification:ai:autonomous` permission; a **verification second pass** agrees; and the tenant has passed the calibration gate (D-B). Implemented as a leader-gated worker (mirror the W-series `*Worker` pattern). **Abandoned signal:** no `typify` submission within a per-tenant ACW deadline after the conversation enters wrap-up; the worker scans for overdue wrap-ups (no new SSE/event needed). **This is built in P2b but ships disabled-by-default**; turning it on is per-binding + permission-gated.

### D-B — Calibration as an enforced gate (the spine)
- New `AiMode.Shadow`: the classifier runs and **persists** its suggestion but never surfaces/applies it.
- New table `typification_ai_suggestions` (migration `004`) stores **every** suggestion regardless of mode: `(id, tenant_id, conversation_id, schema_id, schema_version, suggested_leaf_node_id, suggested_node_path jsonb, suggested_field_values jsonb, confidence, sentiment, model_id, prompt_version, created_at)`. Indexed `(tenant_id, conversation_id)` and `(tenant_id, schema_id, created_at DESC)`.
- On `POST /typify`, the server **looks up the stored suggestion** for the conversation and derives provenance authoritatively: `AiAccepted` = (suggested leaf == committed leaf), and stores the **suggested vs committed** pair (the correction signal). Client-sent `AiSuggested/AiConfidence/AiAccepted` become *hints only* — the server is the source of truth.
- **Gate:** `AutoFill` (and `Autonomous`) cannot be enabled for a binding/schema until the tenant has ≥ `MinCalibrationSamples` (default 200, configurable) reconciled suggestions at the configured threshold with acceptance ≥ `MinCalibrationAccuracy` (default 0.85). Enforced **server-side** on the config-write path **and** reflected in the admin UI (the Mode selector unlocks `AutoFill` only when calibration data clears the bar). `Autonomous` requires a higher bar (default 0.95).

### D-C — Prompt-injection mitigation (mandatory before any auto-fill)
- In `BuildTranscriptText`/`BuildSystemPrompt`: wrap the transcript in an explicit untrusted-data fence, instruct the model to treat it strictly as data (never as instructions), and **neutralize role-marker injection** (strip/escape lines that mimic `Agent:`/`System:` framing).
- Keep and rely on the existing **output allow-list** (leaf must exist, be a leaf, be within `subtreeRoot`); classify by stable `Code`.
- The **autonomous** tier adds a **verification second pass** (independent prompt asking the model to confirm the leaf from the transcript) — disagreement → fall back to suggest/none, never commit.

### D-D — Audit trail + reconstructable provenance (GDPR Art. 22)
- Every AI decision (suggestion produced, auto-fill applied & committed, autonomous commit) emits an `IAuditService` entry. Introduce an **`ai` actor type** (alongside `user`/`system`); the entry captures `model_id`, `prompt_version`, `schema_version`, `confidence`, gating outcome, and suggested-vs-committed leaf.
- The `typification_ai_suggestions` row is the reconstructable record (what was decided, on what input version, with what confidence). Compute the currently-null `AuditEntry.IntegrityHash`.

### D-E — Cost governance + observability (substrate; fixes shipped bugs)
- **Token capture:** add `usage {prompt_tokens, completion_tokens, total_tokens}` to the chat-completions wire response and surface it on `LlmResponse` (today discarded at deserialization).
- **Meter** `verbara.platform.llm` (mirror the `verbara.platform.jwt` pattern; register via `.AddMeter(...)` in `Program.cs` OTel): `llm.request.latency`, `llm.tokens.in/out`, `llm.requests`, `llm.errors`, `llm.suggestion.{made,accepted,overridden}`, `llm.autonomous_commits`, `llm.fail_closed`. Plus `[LoggerMessage]` events on provider failure and degrade-to-null (today both swallowed silently).
- **Fix the resilience NoOp bug:** register the `llm.completions` keyed `ResiliencePolicy` (circuit-breaker + retry + timeout) in `AddPlatformLlm`/`Program.cs`, mirroring `flow.http-request`. (Spec §3.1 of P2a claimed this existed; the keyed policy is never registered → silent `NoOp`.)
- **Budget cap:** per-tenant daily token/call budget (deployment default + a simple per-tenant override value stored alongside AI settings; the *encrypted per-tenant provider config* is P2c). On exceed → degrade to `SuggestOnly`/`Off` + a counter + audit. **LLM-specific rate limit** on the suggestion endpoint (`RequireRateLimiting` with an LLM policy), independent of the generic tenant API limiter.
- **Prompt-size guard:** cap candidate-leaf enumeration (lean on `subtreeRoot`); scale `ClassifyMaxTokens` with field count so entity prefill output isn't silently truncated.

### D-F — Entity prefill + PII policy
- Classifier extracts named entities and maps them to fields via `EntityFieldMap` + `PrefillRef.AiEntity`; `DefaultTypificationPrefillResolver` stops `continue`-ing past non-`Metadata` kinds and resolves `AiEntity`.
- **PII allow-list policy:** a per-tenant policy defines which entity types may be persisted vs must be masked/tokenized. Default-deny for sensitive types (card, national-id/SSN); admin opt-in to store. Tighten `Text/Textarea/Lookup` value validation on the AI write path (length caps + PII screen) — today these field types are written unvalidated.

### D-G — Per-binding AI policy override
Add an optional `AiConfig?` override to `SchemaBinding`; resolution is most-specific-wins (mirror the existing binding precedence). Lets an operator pilot `AutoFill` on a single queue/channel/campaign without cloning the taxonomy. `ResolvedTypification` carries the effective (binding-overridden) `AiConfig`.

### D-H — Multilingual hardening
Classify by stable node `Code` (never by localized label text); detect transcript language; feed labels to the model in a consistent language; tolerate code-switching (es-419 ⇄ en). Prompt/label-consistency work — **not** a new ML pipeline.

### D-I — AutoFill UX anti-clobber
Web tracks a `formDirty` flag. Suggestion arrives async: if the agent has **not** touched the form → apply (auto-fill) + show a dismissible "AI auto-applied (confidence X%) — Undo" affordance; if the agent **has** touched it → degrade to the P2a Accept banner (never overwrite). Confidence badge always shown. A 402 (unlicensed) hides all AI affordances. **Never auto-submit from the client.**

### D-J — RBAC
Introduce a typification permission family (none exists today): `typification:ai:configure` (enable AI / `AutoFill` / thresholds) and `typification:ai:autonomous` (enable no-human commit). Seed into the relevant role templates (`RoleTemplateSeeder`). Admin config endpoints `RequirePermission(...)` accordingly.

## 3. Data / domain changes
- `TypificationAiConfig`: `Mode` (extended enum), `SuggestThreshold`, `AutoApplyThreshold`, `AutonomousThreshold`, `SentimentGating`, `EntityFieldMap`, `PiiPolicy`, `Autonomous` (bool), budget fields. (JSONB on the schema/binding definition — dynamic data, static types, AOT-safe.)
- `AiMode`: `Off | Shadow | SuggestOnly | AutoFill` (+ migrate `AutoApplyAboveThreshold`→`AutoFill`).
- `SchemaBinding`: optional `AiConfig?` override.
- `TypificationSubmission`: add `SuggestedLeafNodeId` / `SuggestedNodePath` (server-derived correction signal); keep `AiSuggested/AiConfidence/AiAccepted` but set them server-side.
- New migration `004_typification_ai_suggestions.sql` (the suggestion/shadow store above). Note: the baseline is already squashed (`001_Baseline.sql`); this is an additive forward migration.

## 4. API changes
- `POST /conversations/{id}/typification-suggestion` (existing): now persists the suggestion (D-B), enforces the band/gating server-side, runs the injection-hardened prompt, returns confidence band + sentiment; gated `AdvancedTypification + TypificationAi`, `RequireRateLimiting(llm)`.
- `POST /conversations/{id}/typify` (existing): server-side provenance derivation + audit (D-B/D-D).
- Admin `typification` endpoints: expose the new `AiConfig` fields + per-binding override + the calibration status (samples, accuracy, gate state); `RequirePermission(typification:ai:configure)`. Entity-field-map editor DTO.
- (Autonomous worker is internal — no public endpoint; supervisor visibility of autonomous commits reuses existing audit/conversation views.)

## 5. Web changes
- Wrap-up (`dynamic-typification-form.tsx`): `formDirty` guard + auto-fill-with-Undo (D-I) + always-on confidence badge.
- Admin designer (`schema-designer-page.tsx`): unlock Mode selector (gated on calibration), the two/three thresholds, `EntityFieldMap` editor, PII policy, per-binding AI override (`binding-form-sheet.tsx`), and a **calibration status panel** (sample count, accuracy, "AutoFill ready" state). Hooks in `use-typification.ts`.
- i18n ×3 (es-419 baseline + en-US + pt-BR).
- (Accuracy *dashboard* — the rich visualization — is **P4**; P2b only captures the signal + shows the minimal calibration status needed to gate AutoFill.)

## 6. AOT / errors / events
- All new DTOs/JSON (usage block, suggestion record, AiConfig fields, verification-pass payload) in source-gen contexts (`ApiJsonContext` / the Llm + Typification JSON contexts). `<IsAotCompatible>` preserved; Native AOT publish must stay 0-warning.
- **Graceful degradation everywhere:** provider down/timeout/malformed/over-budget/unknown-leaf → empty suggestion (agent classifies manually; wrap-up never breaks). Now *observable* (meter + log), not silent.
- No new cross-pod SSE event in P2b (suggestion stays request/response; the autonomous worker is leader-gated like the W-series). (Late voice suggestion via SSE is P2d.)

## 7. Non-goals (explicitly deferred)
- **Voice enrichment** (transcript/sentiment/`DispositionCode` source, post-call async trigger, `Conversation→SessionId` linkage, real `CallSummarizer`) → **P2d**.
- **Per-tenant LLM provider config** (encrypted credentials, BYO-vs-platform-key, provider/model per tenant, health-check, data-residency/no-train flags, full cost attribution to `Billing`) → **P2c**. (P2b ships only a per-tenant budget *value*.)
- **External data-dips** → P3. **Accuracy/quality dashboard + designer polish** → P4. **Streaming suggestions** → cut (YAGNI; single fast round-trip).

## 8. Verification
- Platform: build 0/0 `-warnaserror`; `dotnet test` (Llm, Typification, Flows, Api); **Native AOT publish 0 warnings**. New unit tests: band logic (none/suggest/autofill by confidence); shadow persistence + server-side reconciliation/accuracy; calibration gate (blocks AutoFill until samples+accuracy clear); injection neutralization (role-marker stripped; steering attempt → still allow-list-bounded); verification-pass disagreement → no autonomous commit; budget exceed → degrade + counter; resilience policy registered (circuit-breaker fires); entity extraction + PII allow-list (sensitive type masked unless opted-in); per-binding override precedence; audit entry emitted with `ai` actor + provenance.
- Web: build/lint/i18n parity/vitest; anti-clobber (dirty form not overwritten); calibration-gated Mode selector.
- Manual E2E: shadow mode N conversations → calibration panel fills → unlock AutoFill on one binding → high-confidence digital convo auto-fills with Undo → submit records server-derived provenance + audit; unlicensed tenant → no AI affordance; over-budget tenant → degrades to manual.

## 9. Critical files
- Domain: `src/Verbara.Platform.Typification/{TypificationAiConfig,AiMode,TypificationSchema,SchemaBinding,TypificationSubmission,SubmissionSource}.cs`, `Resolution/ResolvedTypification.cs`, `Prefill/{PrefillRef,PrefillSourceKind,DefaultTypificationPrefillResolver}.cs`, `Ai/{ITypificationAiClassifier,DefaultTypificationAiClassifier}.cs` + new `Ai/AiSuggestionStore` + calibration service + `Ai/PiiPolicy`.
- LLM: `src/Verbara.Platform.Llm/{ILlmProvider,LlmProviderOptions,OpenAiCompatibleLlmProvider,Wire/ChatCompletionWire,ServiceCollectionExtensions}.cs` (usage capture, meter, keyed resilience registration).
- Api: `Endpoints/{ConversationEndpoints,TypificationEndpoints}.cs`, `Serialization/ApiJsonContext.cs`, `Program.cs` (AddMeter, resilience policy, rate-limit policy, autonomous worker), `Middleware/*` (rate-limit), audit wiring.
- Storage: `Storage.Postgres/Migrations/004_typification_ai_suggestions.sql` + the suggestion store; `Storage.InMemory` mirror.
- Identity: `Storage.Postgres/Seeds/RoleTemplateSeeder.cs` (+ permission constants) for `typification:ai:*`.
- Web: `src/agent/conversation/dynamic-typification-form.tsx`, `src/admin/typification/*` (designer, binding sheet, calibration panel, entity-field-map editor), `src/core/api/hooks/use-typification.ts`, `public/locales/{es-419,en-US,pt-BR}/*.json`.

## 10. Suggested implementation phasing (FCM, for the plan)
- **A — Foundation:** token-usage capture + `verbara.platform.llm` meter + `[LoggerMessage]` + register `llm.completions` resilience policy + `typification_ai_suggestions` migration + suggestion store + `typification:ai:*` RBAC.
- **B — Calibration + provenance:** Shadow mode + server-side reconciliation + accuracy computation + audit (`ai` actor + IntegrityHash) + calibration gate.
- **C — AutoFill:** band logic + server-side enforcement + injection hardening + anti-clobber UX + confidence display + Mode selector unlock.
- **D — Entity prefill + PII:** extraction + `EntityFieldMap`/`AiEntity` resolution + PII allow-list + value validation + admin editor.
- **E — Breadth/hardening:** per-binding override + multilingual + budget + LLM rate-limit + autonomous-commit worker (verification pass, ships disabled).
