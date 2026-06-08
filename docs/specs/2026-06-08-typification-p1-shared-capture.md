# Spec — Typification P1: Shared Taxonomy Capture (end-to-end)

> Phase P1 of [ADR-0029](../decisions/0029-typification-cascading-conditional-ai-module.md). Umbrella spec: [2026-06-07-typification-cascading-conditional-ai.md](2026-06-07-typification-cascading-conditional-ai.md). Builds on P0 (shipped 2026-06-07). **Platform + Web only** — no Pro/Sdk change.

## 1. Problem

After P0, typification is **manual at close**: the agent classifies from scratch even though the IVR/bot/routing already knew the customer's reason. P1 threads that knowledge to the wrap-up so the cascade is **pre-selected** and fields **pre-filled**; the agent confirms/adjusts.

## 2. Architecture — the attribute-bag contract

A single string→string contract on `Conversation.Metadata` (the model used by Amazon Connect *contact attributes*, Genesys *participant data*, Talkdesk *context*): every capture source writes well-known keys; **one** consumer reads them at wrap-up.

- **`reasonPath`** = JSON array of node **`Code`s** (root→leaf). `Code` is stable across schema republish; the consumer maps Codes→NodeId against the resolved schema. Shared constant `TypificationMetadataKeys.ReasonPath = "reasonPath"`.
- **Arbitrary prefill keys** (`patientId`, `orderId`, …) consumed by fields with `PrefillSource{Kind:Metadata, Ref:"<key>"}` (the `PrefillRef` placeholder shipped unused in P0).
- **Precedence by execution order** (no special logic): implicit (routing) stamps first → explicit (bot/flow) overwrites later.

## 3. Capture writers

| # | Writer | Mechanism |
|---|--------|-----------|
| B1 | flow-vars → metadata (foundational) | `BotResponse`/`FlowStepResult` gain `IReadOnlyDictionary<string,string>? FlowMetadata`; the engine populates it from `execution.Variables` whose key does not start with `__`; the bot handoff appliers (`WebhookEndpoints`, `WebChatInboundRouter`) `conversation.SetMetadata(k,v)` before transfer. Enables field prefill for any captured value for free. |
| B2 | explicit `collect_reason` node | New `IFlowNodeHandler` (`NodeType "collect_reason"`); config `schema_id` + optional `subtree_root_node_id`; renders cascade children (filtered by `TypificationNode.ChannelApplicability`) as a numbered menu, advances one level per inbound message (partial state in `__reason_*`), writes `reasonPath` Codes JSON at leaf. |
| B3 | implicit digital | `ReasonHintMiddleware` (continuation-first; reads the resolved `QueueId`, resolves a `ReasonHint`, merges `reasonPath` into the previously-unused `RouteResult.Metadata`); appliers copy `RouteResult.Metadata` onto the conversation. |
| B4 | implicit voice | `StasisInboundConsumer` (already has DID + DidRoute) resolves a `ReasonHint` and sets channel var `VERBARA_REASON`; `VoiceConversationBridge.OnCallQueuedAsync` reads it via AMI GetVar (same pattern as `TENANT_ID`) and stamps `reasonPath` metadata. |

## 4. New domain — `ReasonHint`

```
ReasonHint : ITenantScoped {
  EntityId HintId; TenantId TenantId;
  ReasonHintScope Scope;   // Did | Channel | Queue
  string ScopeRef;         // the DID (E.164) | ChannelType name | queueId
  string ReasonPath;       // JSON array of node Codes
  int Priority; bool IsActive;
}
```

`IReasonHintResolver.ResolveAsync(string? did, EntityId? queueId, ChannelType channel)` → most-specific-wins **Did → Queue → Channel**, `IsActive` only, tiebreak `Priority` desc then `HintId` ordinal. Kept separate from `DidRoute` (routing vs reason = SoC). Persisted in a TEXT-column table `reason_hints` (no JSONB) — InMemory + Postgres stores mirroring the binding stores.

## 5. Consumer — prefill resolver

`ITypificationPrefillResolver.ResolvePrefill(TypificationSchema schema, EntityId? subtreeRoot, Conversation conversation)` → `PrefillResult(IReadOnlyList<EntityId> PrefilledNodePath, IReadOnlyDictionary<string,string> PrefilledFieldValues)`:

- Read `Metadata["reasonPath"]` (Codes JSON, source-gen deserialize). Build `Code→NodeId` from `schema.Nodes`. Walk validating the parent-child chain; take the **longest valid prefix**; respect `subtreeRoot` (path must descend from it). **Never throws** (missing/malformed → empty path).
- For each `TypificationField` with `PrefillSource?.Kind == Metadata`, read `conversation.Metadata[PrefillSource.Ref]` if present.

`GET /conversations/{id}/typification-form` extends its response with `PrefilledNodePath?` (node-id strings) + `PrefilledFieldValues?`. `POST /typify` is **unchanged** (still server-authoritative). Runtime endpoints stay **un-gated** (an unlicensed tenant must still close conversations — P0 rule).

## 6. Migration

`002_reason_hints.sql` (first additive migration after P0's `001_Baseline`): `reason_hints(tenant_id, hint_id, scope, scope_ref, reason_path, priority, is_active, PK(tenant_id, hint_id))` + index `(tenant_id, scope, scope_ref)`. Discovered by `DatabaseMigrationService` (embedded glob, ordinal order).

## 7. Web

- **Flow casing fix** (`flow-utils.ts`): pure bidirectional PascalCase↔snake_case mapping (`toDomain`→snake, `toReactFlow`→Pascal) — the engine vocabulary is snake_case; PascalCase is a React-Flow render detail. Fixes `collect_reason` and all designer nodes (latent bug: designer-built flows threw at runtime).
- **`collect_reason` designer node** (component + registry + palette + property panel with schema/subtree pickers).
- **Wrap-up prefill hydration** (`dynamic-typification-form.tsx`): seed `selectedNodePath` from `prefilledNodePath` (strip ancestor prefix up to & incl. `subtreeRootNodeId` to match the subtree-relative UI path) + `fieldValues` from `prefilledFieldValues`; one-shot on load; agent can override.
- **Admin `/admin/reason-hints`** page + `use-reason-hints` hook + lazy route `PermissionGuard requires="system:typification:configure"` + sidebar item.
- **i18n ×3** (es-419 baseline + en-US + pt-BR; CI parity gate).

## 8. Licensing

Admin only (`/admin/reason-hints` CRUD + the `collect_reason` configuration in the designer) is gated `RequireLicenseFeature(AdvancedTypification)` (already exists from P0) + Web `PermissionGuard system:typification:configure`. Runtime capture/consumption is never gated.

## 9. Non-goals (deferred)

Pre-chat WebChat capture; voicebot ASR/NLU reason; `ai_classify`-as-reason (→ P2); in-platform voice DTMF IVR engine; external data-dips (→ P3); prefill provenance analytics + drag-drop builder (→ P4).

## 10. Verification

Build `-warnaserror` 0; `dotnet test` (Typification, Flows, Bot, Routing.Inbound, Storage.InMemory, Storage.Postgres, Api); `dotnet publish` 0 AOT warnings; clean-DB migration assertion; Web `build`/`lint`/`i18n:check`/`vitest`. Manual E2E exercises all four writers + prefill consumption + 402 gating (see plan).
