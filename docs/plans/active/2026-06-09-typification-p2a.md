# Plan — Typification P2a: AI auto-disposition (provider + core SuggestOnly)

## Context

After P0 (cascading/conditional) + P1 (shared capture), the wrap-up cascade is pre-selected from *captured* context but the agent still classifies the disposition manually. **P2a** adds AI auto-disposition: an LLM reads the conversation and **suggests** the disposition node path + field values + confidence at wrap-up; the agent confirms/overrides. Deep analysis found there is **no existing real LLM client** in the ecosystem (the `ILlmProvider` seam had only the P1 `DisabledLlmProvider` stub), and the Pro AI stack yields flat/voice-only outputs — so P2's core is a **direct, channel-agnostic LLM classifier**, with Pro `CallAnalytics`/`AgentAssist` as optional voice enrichment in **P2b**. Approved spec: [`docs/specs/2026-06-09-typification-p2-ai-auto-disposition.md`](Verbara.Platform/docs/specs/2026-06-09-typification-p2-ai-auto-disposition.md) + ADR-0029 P2 addendum. **Cross-repo Pro + Platform + Web** (new Pro license flag — P0-style release). Already on branch `feat/typification-p2a` (spec/ADR committed `81cb5b34`).

## Decisions (approved)
1. **Direct LLM classifier at wrap-up** is the core (channel-agnostic); Pro stack = P2b enrichment.
2. **New `Verbara.Platform.Llm`** project (open, AOT) owns `ILlmProvider` (moved out of Flows to break the `Flows→Typification` cycle) + `OpenAiCompatibleLlmProvider` + `LlmProviderOptions` + `DisabledLlmProvider`. Covers OpenAI/Azure/local by base-URL+model.
3. **Async** `POST /conversations/{id}/typification-suggestion`; form stays instant.
4. **New Pro flag `LicenseFeature.TypificationAi = 1 << 10`**; AI gated `AdvancedTypification + TypificationAi`. LLM provider itself is open/ungated.
5. **P2a = SuggestOnly**; AutoApply + entities (`AiEntity`/`EntityFieldMap`) + Pro voice enrichment + per-tenant LLM config = **P2b**.

## Conventions
Subagent-Driven + FCM; Conventional Commits, **NO Co-Authored-By/AI refs**; Native AOT (no reflection; source-gen JSON), `TreatWarningsAsErrors`/WL 9999; cross-repo Pro pack→feed→restore; test naming `Method_ShouldExpected_WhenCondition`; **confirm before push/PR/merge**; holística cross-repo final. No new cross-pod SSE event. Graceful degradation on every AI error (wrap-up never breaks).

## Confirmed integration points (read)
- Pro `FeatureRegistry.GetCanonicalFeaturesForTier` (`:42`): paid tiers = `LicenseFeature.All`; `SelfHostStartup` (`:45-50`) is an explicit subset incl. `AdvancedTypification`. `LicenseFeature` highest bit = `1<<9`. `PublicApi.Unshipped.txt` exists.
- `Program.cs`: `AddPlatformBot()` `:179`, `AddPlatformFlows()` `:183`, `AddPlatformTypification()` `:189` → insert `AddPlatformLlm()` at ~`:182` (before Flows so the real provider wins the Flows `TryAddSingleton<ILlmProvider, DisabledLlmProvider>` stub).
- Resilience pattern: `HttpRequestNodeHandler` uses keyed `ResiliencePolicy` (`"flow.http-request"`, `FromKeyedServices`, defaults `ResiliencePolicy.NoOp`) + `services.AddHttpClient<HttpRequestNodeHandler>()` — mirror for the LLM provider (`"llm.completions"`).
- `ILlmProvider`/`LlmRequest`/`LlmMessage`/`LlmResponse`/`DisabledLlmProvider` live in `Flows`; referenced by `AiClassifyNodeHandler`, `AiGenerateNodeHandler`, `ServiceCollectionExtensions` → moving to `Llm` = add ProjectReference + fix `using`.
- P0/P1 hooks (verbatim, ready): `TypificationAiConfig{Enabled,Mode,ConfidenceThreshold,SentimentGating,EntityFieldMap}` + `AiMode{SuggestOnly,AutoApplyAboveThreshold}` (persisted in `TypificationSchemaDefinition` JSONB); `TypificationSubmission{AiSuggested,AiConfidence,AiAccepted,Source}`; `ConversationEndpoints.cs:261-277` hardcodes `AiSuggested=false`/`Source=Manual` (P2a wires these); `TypifyRequest` (`:204` region) + `TypificationFormResponse` (`:436`); `IMessageStore.GetConversationMessagesAsync(tenant,convId,limit,offset,ct)` → `Message.Content.Blocks` (`TextBlock.Text`); `DefaultTypificationPrefillResolver` (the prefill precedent); `TypificationNode{Code,Label,ParentNodeId,IsLeaf,ChannelApplicability,Leaf{Category}}`.

---

## PHASE A — Pro license flag 🔒 (cross-repo sequence first)
- **A1** `Verbara.Sdk.Pro.Licensing/LicenseFeature.cs`: `TypificationAi = 1 << 10` + add to `All` mask. `FeatureRegistry.cs`: `All`-mapped tiers get it automatically; add `| LicenseFeature.TypificationAi` to the `SelfHostStartup` explicit list (mirror `AdvancedTypification` `:50` — **note:** if AI should be higher-tier-only, drop from SelfHostStartup; default = include). `PublicApi.Unshipped.txt` append. Tests: `All_ShouldContainTypificationAi`, `GetCanonicalFeaturesForTier_ShouldIncludeTypificationAi_When{Developer,SelfHostStartup}`, validator canonical-match.
- **A2 🔒** Pack Pro → `/media/Data/Source/Verbara/local-nuget-feed/` → `rm -rf ~/.nuget/packages/verbara.sdk.pro*` → `dotnet restore` Platform. Gate: `LicenseFeature.TypificationAi` resolves in Platform. (New Pro version cut on release — Platform pins bump then.)

## PHASE B — `Verbara.Platform.Llm` (new open AOT project) 🔒
- **B1 🔒** Create `src/Verbara.Platform.Llm/` (`<IsAotCompatible>true>`; deps: Sdk.Resilience + Microsoft.Extensions.{Http,Options}). **MOVE** `ILlmProvider`+`LlmRequest`/`LlmMessage`/`LlmResponse`+`DisabledLlmProvider` from Flows → here (namespace `Verbara.Platform.Llm`). Add project to solution. `Flows`: add ProjectReference to `Llm`; fix `using` in `AiClassifyNodeHandler`/`AiGenerateNodeHandler`/`ServiceCollectionExtensions` (the `TryAddSingleton<ILlmProvider,DisabledLlmProvider>` stays in Flows' SCE referencing the moved types). Gate: solution builds, Flows.Tests green.
- **B2 🔒** `OpenAiCompatibleLlmProvider` (POST `{BaseUrl}/chat/completions`; `LlmJsonContext` source-gen for request/response; keyed `ResiliencePolicy "llm.completions"` default NoOp + per-call timeout) + `LlmProviderOptions{BaseUrl,ApiKey,Model,Temperature=0.2,MaxTokens=800,TimeoutSeconds=20}` + `AddPlatformLlm(Action<LlmProviderOptions>?)` — registers the provider as `ILlmProvider` (plain `AddSingleton`, wins Flows' `TryAdd`) **only when BaseUrl+ApiKey+Model are set**, else no-op (stub stays). `AddHttpClient<OpenAiCompatibleLlmProvider>()`. Tests (`Verbara.Platform.Llm.Tests`): `CompleteAsync_ShouldPostChatCompletionAndParseContent_WhenProviderResponds` (fake `HttpMessageHandler`); `CompleteAsync_ShouldThrowOrSurface_WhenHttpError` (resilience); `AddPlatformLlm_ShouldRegisterRealProvider_WhenConfigured` / `_ShouldLeaveStub_WhenUnconfigured`.
- **B3** Wire `builder.Services.AddPlatformLlm(o => bind from config "Llm" section)` in `Program.cs` ~`:182` (before `AddPlatformFlows()`).

## PHASE C — Typification AI classifier 🔒
- **C1 🔒** `Typification.csproj`: add ProjectReference to `Llm` (no cycle — Llm has no deps). `src/Verbara.Platform.Typification/Ai/{ITypificationAiClassifier,DefaultTypificationAiClassifier}.cs` + `AiClassification(IReadOnlyList<EntityId> NodePath, IReadOnlyDictionary<string,string> FieldValues, double Confidence, string? Sentiment)` + `TypificationAiJsonContext` (source-gen for the `{leafCode,confidence,sentiment,fields}` parse shape). `ClassifyAsync(schema, subtreeRoot, conversation, transcript, ct)`: build transcript text (by direction) + prompt enumerating channel-filtered **leaf** Codes/Labels/path + field defs → `ILlmProvider.CompleteAsync` → parse JSON defensively (**never throws**: malformed/empty/unknown-leaf/non-leaf → null) → map `leafCode`→root→leaf NodeId path (walk parents) → validate leaf + within subtree. Register in `AddPlatformTypification`. Tests (`Typification.Tests`, fake `ILlmProvider`): `ClassifyAsync_ShouldReturnValidatedPath_WhenLlmReturnsValidLeafCode`, `_ShouldReturnNull_When{MalformedJson,UnknownLeafCode,NonLeafCode}`, `_ShouldSurfaceConfidenceAndSentiment`, `_ShouldExtractFieldValues`, `_ShouldRespectSubtreeRoot`.

## PHASE D — Api: suggestion endpoint + provenance + AiConfig DTO
- **D1 🔒** `ConversationEndpoints.cs`: `POST /conversations/{id}/typification-suggestion` (group gated `RequireLicenseFeature(AdvancedTypification)` + `RequireLicenseFeature(TypificationAi)`) → resolve schema → if `!AiConfig.Enabled`/no provider/no schema → 200 empty; load transcript (`IMessageStore`) → if no text → 200 empty (voice = P2b); classify → gate: `ConfidenceThreshold` (below→empty), `SentimentGating` (very-negative → drop a `TypificationCategory.Success` leaf), `SuggestOnly`. Return `TypificationSuggestionResponse{SuggestedNodePath?(string[]),SuggestedFieldValues?,Confidence?,Sentiment?}`. DTOs in `ApiJsonContext`. Tests: `_ShouldReturnSuggestion_WhenConfiguredLicensedWithTranscript`, `_ShouldReturn402_WhenTypificationAiUnlicensed`, `_ShouldReturnEmpty_When{AiDisabled,NoTranscript,NoSchema}`, `_ShouldDropSuccessLeaf_WhenSentimentVeryNegativeAndGated`, `_ShouldReturnEmpty_WhenBelowConfidenceThreshold`.
- **D2** Extend `TypifyRequest` with `bool? AiSuggested, double? AiConfidence`; `/typify` sets `AiSuggested/AiConfidence/AiAccepted` + `Source = AiSuggested==true ? AutoAi : Manual`. Tests: `Typify_ShouldRecordAutoAiProvenance_WhenAiSuggestionAccepted`, `_ShouldRecordManual_WhenNoAi`.
- **D3** `TypificationEndpoints.cs`: expose `aiConfig{enabled,mode,confidenceThreshold,sentimentGating}` on the schema admin DTO (create/update map + `ToSchemaDto`; `entityFieldMap` pass-through, edited in P2b). Register DTO in `ApiJsonContext`. Tests: `CreateSchema_ShouldPersistAiConfig_WhenProvided`, `_ShouldDefaultAiConfigDisabled_WhenOmitted`.

## PHASE E — Web
- **E1** `src/core/api/hooks/use-typification.ts`: `useTypificationSuggestion(conversationId)` (POST) + `TypificationSuggestionResponse` type; extend `TypifyInput` with `aiSuggested?`/`aiConfidence?`; extend schema types with `aiConfig`.
- **E2 🔒** `src/agent/conversation/dynamic-typification-form.tsx`: after form load, call the suggestion endpoint → "AI analizando…" spinner → render suggested cascade path + **confidence badge** + sentiment → **Accept** (prefill cascade via the existing hydration path + set `aiAccepted/aiSuggested/aiConfidence` on submit) / ignore. 402 → silently hide the AI affordance.
- **E3** `src/admin/typification/*`: AiConfig section in the schema designer (enabled toggle + mode [SuggestOnly fixed in P2a] + confidenceThreshold + sentimentGating), wired through the schema mappers/DTO.
- **E4** i18n ×3 (es-419 baseline + en-US + pt-BR; `npm run i18n:check`).

## PHASE F — AOT + holística
- 🔒 Native AOT publish (`-p:PublishAot=true -r linux-x64 -p:InvariantGlobalization=true`) → **0 IL/trim warnings** (validates `Llm` + new DTOs source-gen, no managed `Verbara*.dll`). Holística cross-repo (W4/W5b-class: wire/AOT/empty-body/license/graceful-degradation; verify the classifier never throws into the wrap-up; verify the suggestion endpoint is gated but `/typify`+`/form` are not).

## FCM ordering
🔒A1→🔒A2 (Pro) ⟶ 🔒B1→🔒B2→B3 (Llm) ⟶ 🔒C1 ⟶ 🔒D1→D2→D3 ⟶ E1→(🔒E2 ∥ E3)→E4 ⟶ 🔒AOT + **holística**.

## Verification
- **Pro:** build `-warnaserror` + licensing tests; pack→feed→restore resolves `TypificationAi`.
- **Platform:** build `-warnaserror` 0/0; `dotnet test` (Llm, Typification, Flows, Api); **AOT publish 0 warnings**. (No real LLM key in dev → provider tested via fake `HttpMessageHandler`, classifier via fake `ILlmProvider`; the live-endpoint E2E is documented, run where a key exists.)
- **Web:** `npm run build` + `lint` + `i18n:check` + `vitest`.
- **Manual E2E (where an OpenAI-compatible endpoint is configured):** enable AiConfig on a schema → digital conversation with text → open wrap-up → suggestion appears with confidence → Accept prefills → `/typify` records `Source=AutoAi`; unlicensed (`TypificationAi` off) → no AI affordance (402 hidden), manual wrap-up unaffected; `ai_classify`/`ai_generate` flow nodes now function with the real provider.

## Critical files
- Pro: `Verbara.Sdk.Pro.Licensing/{LicenseFeature,FeatureRegistry}.cs` (+ PublicApi.Unshipped.txt)
- Platform new: `src/Verbara.Platform.Llm/**` (ILlmProvider moved + `OpenAiCompatibleLlmProvider` + `LlmProviderOptions` + `DisabledLlmProvider` + SCE + `LlmJsonContext`) · `src/Verbara.Platform.Typification/Ai/**` (+ `TypificationAiJsonContext`)
- Platform mod: `src/Verbara.Platform.Flows/{ILlmProvider.cs removed, ServiceCollectionExtensions.cs, Nodes/Ai*NodeHandler.cs}` · `src/Verbara.Platform.Api/Endpoints/{ConversationEndpoints,TypificationEndpoints}.cs` · `Serialization/ApiJsonContext.cs` · `Program.cs`
- Web: `src/core/api/hooks/use-typification.ts` · `src/agent/conversation/dynamic-typification-form.tsx` · `src/admin/typification/**` · `public/locales/{es-419,en-US,pt-BR}/*.json`

## Docs / release
ADR-0029 P2 addendum + P2a spec already committed. On ship: mirror this plan to `docs/plans/active/2026-06-09-typification-p2a.md` → `completed/` on merge; mark umbrella spec P2a done. Release = cross-repo coordinated (Pro vNext-pro → Platform 2.12.0 + Web 3.8.0 pins/tags/signed-images → website digest), P0-style, decided after merge.
