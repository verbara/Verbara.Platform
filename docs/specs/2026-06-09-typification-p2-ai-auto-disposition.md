# Spec — Typification P2a: AI Auto-Disposition (provider + core SuggestOnly)

> Phase **P2a** of [ADR-0029](../decisions/0029-typification-cascading-conditional-ai-module.md) (see the P2 addendum). Umbrella: [2026-06-07-typification-cascading-conditional-ai.md](2026-06-07-typification-cascading-conditional-ai.md). Builds on P0 (cascading/conditional) + P1 (shared capture). **Cross-repo: Pro + Platform + Web** (new Pro license flag). P2b (AutoApply + entities + Pro voice enrichment) is a fast-follow.

## 1. Problem

After P0/P1 the wrap-up cascade is pre-selected from *captured context* (IVR/routing reasonPath), but the agent still classifies the **disposition** manually. P2 adds **AI auto-disposition**: an LLM reads the conversation and **suggests** the disposition node path + field values + confidence at wrap-up; the agent confirms or overrides. P0/P1 already shipped the hooks (`TypificationAiConfig`, `TypificationSubmission.AiSuggested/AiConfidence/AiAccepted`, `SubmissionSource.AutoAi`, `PrefillRef.AiEntity`, `EntityFieldMap`) — all unused until now. **P2a delivers the foundation + the SuggestOnly core; P2b adds AutoApply, entity prefill, and Pro voice enrichment.**

## 2. Key finding (reframe)

There is **no existing real LLM client** anywhere in Verbara — the `ILlmProvider` seam (P1 shipped only a `DisabledLlmProvider` stub) is the first. The Pro AI stack produces flat/voice-only outputs (`CallSummary.DispositionCode` = one post-call string; `AgentAssist` = real-time prose; `ai_classify` = one label) — none yields the cascading root→leaf path + entities + confidence P2 needs across channels. So **P2's core is a direct, channel-agnostic LLM classifier at wrap-up**, with Pro `CallAnalytics`/`AgentAssist` as optional voice enrichment (P2b), not the core.

## 3. Architecture

### 3.1 New project `Verbara.Platform.Llm` (open, AOT)
Resolves a cycle: `Flows → Typification` already exists (P1 `collect_reason`), so `ILlmProvider` cannot be consumed from `Typification` while it lives in `Flows`. **Move** `ILlmProvider` + `LlmRequest`/`LlmMessage`/`LlmResponse` + `DisabledLlmProvider` into `Verbara.Platform.Llm` (no deps). Both `Flows` and `Typification` depend on `Llm` (no cycle). Add:
- **`OpenAiCompatibleLlmProvider`** — `HttpClient` POST to `{BaseUrl}/chat/completions`, source-gen JSON request/response, wrapped in `Sdk.Resilience` (circuit-breaker/retry/timeout). Covers OpenAI + Azure OpenAI + local (Ollama/vLLM/LM Studio) via base-URL + model.
- **`LlmProviderOptions`** `{ BaseUrl, ApiKey, Model, Temperature=0.2, MaxTokens=800, TimeoutSeconds=20 }` — **deployment-level** (env/`IOptions`; per-tenant deferred). Mirror the Pro `Action<Options>` + builder pattern.
- **`AddPlatformLlm(Action<LlmProviderOptions>)`** registers `OpenAiCompatibleLlmProvider` as `ILlmProvider` **before** `AddPlatformFlows()` (whose `TryAddSingleton` stub then loses). If `ApiKey`/`BaseUrl` unset → keep the stub (AI simply unavailable). This **also lights up the dormant `ai_classify`/`ai_generate` flow nodes** (open Platform feature).

### 3.2 `ITypificationAiClassifier` (in `Typification/Ai`)
`ClassifyAsync(TypificationSchema schema, EntityId? subtreeRoot, Conversation conversation, IReadOnlyList<Message> transcript, CancellationToken) → AiClassification?` where `AiClassification(IReadOnlyList<EntityId> NodePath, IReadOnlyDictionary<string,string> FieldValues, double Confidence, string? Sentiment)`.
- Builds a transcript string from `Message` `TextBlock`s tagged by direction.
- Builds a prompt enumerating the schema's **leaf** `Code`+`Label`+path (channel-filtered) and the field definitions to extract; asks the LLM for strict JSON `{ "leafCode": "...", "confidence": 0.0-1.0, "sentiment": "...", "fields": { "<key>": "<value>" } }`.
- Calls `ILlmProvider.CompleteAsync`. Parses defensively with a source-gen context (**never throws** — malformed/empty/unknown-leaf → `null`).
- Maps `leafCode` → full root→leaf `NodeId` path via the schema (walk parents); validates it's a leaf and within `subtreeRoot`; drops if invalid.
- Pure of HTTP except the injected `ILlmProvider`; unit-testable with a fake provider.

### 3.3 Async suggestion endpoint
`POST /conversations/{id}/typification-suggestion` → `TypificationSuggestionResponse { string[]? SuggestedNodePath, IReadOnlyDictionary<string,string>? SuggestedFieldValues, double? Confidence, string? Sentiment }`. Flow: load conversation → resolve schema (binding resolver) → if no schema/`!AiConfig.Enabled`/no provider → empty (200 with nulls). Else load transcript (`IMessageStore.GetConversationMessagesAsync`) → if no text transcript → empty (voice transcript = P2b). Else classify → apply **gating**: `ConfidenceThreshold` (below → no suggestion); `SentimentGating` (never suggest a `TypificationCategory.Success` leaf when sentiment is very-negative); `AiMode.SuggestOnly` (P2a always returns as a suggestion; `AutoApplyAboveThreshold` = P2b). Gated `RequireLicenseFeature(AdvancedTypification)` **+** `RequireLicenseFeature(TypificationAi)`.

### 3.4 Submission provenance
Extend `TypifyRequest` with `bool? AiSuggested`, `double? AiConfidence` (client echoes the suggestion it accepted). `POST /typify` sets `AiSuggested`, `AiConfidence`, `AiAccepted`, and `Source = SubmissionSource.AutoAi` when an AI suggestion was accepted (else `Manual`). (P1 hardcoded `AiSuggested=false`/`Source=Manual` — now wired from the request.)

### 3.5 Licensing — new Pro flag
**`Verbara.Sdk.Pro.Licensing.LicenseFeature.TypificationAi = 1 << 10`** (+ extend the `All` mask + `FeatureRegistry` tiers, mirror P0's `AdvancedTypification`). Pack Pro → bump consumer pin (cross-repo workflow). AI auto-disposition (the suggestion endpoint) is gated `AdvancedTypification + TypificationAi`. The runtime `/typify` + `/typification-form` stay ungated (agents always close). The open LLM provider is not gated.

## 4. Web (P2a)
- **Wrap-up:** after the form renders, `POST .../typification-suggestion` (new `useTypificationSuggestion` hook) → spinner "AI analizando…" → render the suggested node path + a **confidence badge** + sentiment; **Accept** (pre-fills the cascade + marks `aiAccepted` + echoes confidence on submit) / **ignore** (manual). A 402 (unlicensed) silently hides the AI affordance.
- **Schema designer admin:** expose `aiConfig { enabled, mode, confidenceThreshold, sentimentGating }` (P2a: `mode` fixed to `SuggestOnly`; `entityFieldMap` = P2b) — a small section in the designer; DTO `aiConfig` added to the schema admin request/response (already in the JSONB definition, just expose it).
- i18n ×3 (es-419 baseline + en-US + pt-BR).

## 5. AOT / errors / events
- `Verbara.Platform.Llm` `<IsAotCompatible>`; the LLM chat-completions request/response + the classifier's `{leafCode,...}` JSON in **source-gen contexts** (no reflection). New endpoint/admin DTOs in `ApiJsonContext`.
- **Graceful degradation everywhere:** provider unconfigured/down, LLM timeout, malformed JSON, unknown leaf → empty suggestion (the agent classifies manually; the wrap-up never breaks). Resilience + timeout on the provider.
- **No new cross-pod SSE event** (on-demand request/response).

## 6. Non-goals (→ P2b)
`AutoApplyAboveThreshold` enforcement + auto-fill UX; entity prefill (`PrefillRef.AiEntity` + `EntityFieldMap`); Pro voice enrichment (sentiment from `CallAnalytics`, `CallSummary.DispositionCode` hint, voice transcript source); per-tenant LLM config; streaming suggestions.

## 7. Verification
Pro: build `-warnaserror` + licensing tests (`TypificationAi` in `All`/tiers); pack→feed→restore. Platform: build 0/0; `dotnet test` (Llm, Typification, Flows, Api); **Native AOT publish 0 warnings** (validates `Llm` + new DTOs source-gen); classifier tests with a fake `ILlmProvider` (valid JSON → path; malformed → null; unknown leaf → null; below-threshold → gated; very-negative sentiment → no Success leaf). Web: build/lint/i18n/vitest. Manual E2E: configure an OpenAI-compatible endpoint → digital conversation with text → open wrap-up → suggestion appears with confidence → accept pre-fills → submit records `Source=AutoAi`; unlicensed tenant → no AI affordance (402 hidden), manual wrap-up still works.

## 8. Critical files
- Pro: `Verbara.Sdk.Pro.Licensing/{LicenseFeature,FeatureRegistry}.cs` (+ PublicApi.Unshipped.txt).
- Platform: `src/Verbara.Platform.Llm/**` (new: `ILlmProvider` moved here + `OpenAiCompatibleLlmProvider` + `LlmProviderOptions` + `DisabledLlmProvider` + SCE + JSON ctx) · `src/Verbara.Platform.Flows/{ILlmProvider.cs removed, ServiceCollectionExtensions.cs ref Llm}` · `src/Verbara.Platform.Typification/Ai/{ITypificationAiClassifier,DefaultTypificationAiClassifier}.cs` · `src/Verbara.Platform.Api/Endpoints/ConversationEndpoints.cs` (suggestion endpoint + typify provenance) · `Serialization/ApiJsonContext.cs` · `Program.cs` (`AddPlatformLlm` before `AddPlatformFlows`) · `TypificationEndpoints.cs` (AiConfig DTO).
- Web: `src/core/api/hooks/use-typification.ts` (suggestion hook + types) · `src/agent/conversation/dynamic-typification-form.tsx` (suggestion UX) · `src/admin/typification/*` (AiConfig section) · `public/locales/{es-419,en-US,pt-BR}/*.json`.
