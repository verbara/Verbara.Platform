# Spec — Phase 3B.1: Voice Conversation screen-pop in the agent UI

**Status:** Approved (2026-05-31). **D1 = new `VoiceScreenPopEvent`** (correlation keys + contactId for canonical hydration + display hints; deep analysis rejected event-enrichment of `state_changed` and refetch-and-pick-most-recent as racy/invasive). **D2 = auto-navigate to the canonical conversation-panel on answer** (deep analysis rejected click-to-open as defeating screen-pop, and a call-card-hosted panel as duplicating the canonical render). **D3 = INCLUDE agent-assist per-conversation binding now** (scope expanded — see §4.4).
**Date:** 2026-05-31
**Scope:** Backend (`Verbara.Platform.Api`) + React frontend (`Verbara.Platform.Web`). Cross-repo.
**Related:** [3B.0 spec](2026-05-31-voice-conversation-bridge-3b0.md) · [3B plan](../plans/active/2026-05-31-voice-tracked-conversation-3b.md) · epic memory `project_inbound_delivery_epic`

---

## 1. Context

3B.0 shipped `VoiceConversationBridge`: an inbound call becomes a tracked voice `Conversation` (Queued→Offered→Active on answer, owner = the answering agent), and the bridge publishes a tenant-broadcast `ConversationStateChangedEvent`. **That event is insufficient for a screen-pop** — it carries `{TenantId, ConversationId, OldState, NewState}` only: NO `AgentId` (so the browser can't tell *which* agent the call is for), NO contact/channel (so it can't render), and the client `conversation.state_changed` handler only mutates an **already-existing** store entry. So with 3B.0 alone the voice Conversation never surfaces in the agent UI. 3B.1 closes that: when the agent answers, their browser screen-pops the voice Conversation (contact + history + disposition).

### 1.1 Verified surfaces (understand-workflow, 7 readers)

- **SSE targeting (the load-bearing pattern):** the SSE stream `/api/v1/events/stream` is **tenant-scoped only** (`PlatformDeliveryFilter` = tenant + optional `Metadata.UserId`). Agent-targeted events (`conversation.offered`/`conversation.assigned`) do NOT set `Metadata.UserId`; they broadcast tenant-wide carrying an `AgentId` field and the **client filters** via `isForCurrentAgent(data.agentId, currentAgentId())` where `currentAgentId()` reads `Agent.AgentId` from the `['agent-me']` cache (`resolveAgentId = cached?.agentId ?? cached?.id`). This is the P1-confirmed, load-bearing pattern. `VoiceConversationBridge` has `Agent.AgentId` (from `ExtractAgentId`) but **not** `User.UserId` → the AgentId-broadcast + client-filter model is the natural fit (server-side `Metadata.UserId` routing would need a user lookup the bridge lacks).
- **Bridge seam:** `VoiceConversationBridge.OnCallConnectedAsync`, inside the `becameActive` block (after Owner=Agent assigned, ~`VoiceConversationBridge.cs:243`) — `agentId` (EntityId), `tenant`, `conversation.ConversationId`, `conversation.ContactId`, `session.CallerIdNum/CallerIdName` all in scope. This is where 3B.1 fires the screen-pop event.
- **Contact name:** the digital `ConversationAssignedEvent` ships `ContactName=""` — the digital panel hydrates the contact by `conversationId`. For voice the bridge should resolve the display name via `IContactStore.GetByIdAsync(tenantId, conversation.ContactId)` (fallback `session.CallerIdNum`), since 3A left `callerId` empty and `SimpleUser` cannot read the SIP From in-browser (hard limitation — caller identity MUST come from the server).
- **Web call/conversation state:** `voice-call-store` (`idle|ringing|active|ended`, `callerId`/`callerNumber` placeholders, **no** `associatedConversationId` yet — the 3B.1 seam). `conversation-store.upsertConversation` **replaces** the whole record (must spread-merge or it wipes fields — the P1 blank-inbox bug). The `conversation-panel` is messaging-only (MessageThread + ReplyComposer); the icon/label maps already include `'voice'`, but a no-message voice conversation needs a `channel==='voice'` branch that suppresses the thread/composer and shows contact + history. Screen-pop selection is route-driven (`navigate('/agent/conversation/{id}')` + `store.select(id)`); there is NO auto-open today (digital uses a clickable toast).
- **Wrap-up:** `WrapUpDialog` is **channel-agnostic**, keyed by `conversationId`, POSTs `/api/v1/conversations/{id}/wrapup`. 3B.0 already moves the voice conv Active→WrapUp on hangup, so 3B.1 only needs the conv present in the store + (optionally) auto-open the dialog on `state==='wrap_up'`.
- **SSE payload is camelCase** (`ApiJsonContext` `PropertyNamingPolicy=CamelCase`); the client reads `data.agentId`/`data.conversationId`. New `[JsonSerializable]` records go in `ApiJsonContext.cs` (SSE block ~line 65). i18n keys must land in **all three** `agent.json` (en-US, es-419, pt-BR) — CI-blocking parity.

---

## 2. Goals / Non-goals

**Goals (3B.1):**
- G1. When the answering agent's browser is on an inbound voice call, screen-pop the tracked voice `Conversation`: contact identity + history, auto-surfaced.
- G2. The agent disposes the call via the existing wrap-up dialog on hangup.
- G3. The call card becomes conversation-aware (shows the resolved contact, links to the conversation); `callerId`/`callerNumber` populated from the server (3A left them empty).
- G4. Single-agent targeting (only the answering agent screen-pops), reusing the load-bearing `isForCurrentAgent`/AgentId filter.

**Non-goals (deferred):**
- **Live agent-assist (transcript/sentiment/compliance) bound to the voice conversation → DEFERRED to 3B.1b/3B.2.** Reason: `agentassist.*` events are keyed by the AMI `SessionId` (not `conversationId`) and the `agent-ai-store` is a single **global** store; binding them to the voice `ConversationId` requires (a) backend — add `ConversationId` to the four `AgentAssist*Event` records (resolve via `FindByVoiceLinkedIdAsync` in `AgentAssistBridge`) + verify `AgentAssistSession.CallSession.AgentId` is populated for app_queue (the bridge notes it's "often empty"), and (b) frontend — refactor `agent-ai-store` to a per-conversation keyed store + thread `conversationId` through `SuggestionBanner`/`SentimentGauge`/`TranscriptTab`. That is a substantial sub-feature, and **STT is a lab stub** ([3B.0 spec §9 Risk 5]), so live transcript has no real data to show yet. Screen-pop + disposition is the shippable core and works without it.
- In-call control (mute/DTMF/hold/transfer) → 3B.2. Outbound/supervisor → 3B.3.

---

## 3. Design decisions (the forks — see §6 for the questions)

### D1 — Screen-pop event: NEW `VoiceScreenPopEvent` (recommended) vs reuse `ConversationAssignedEvent`
`ConversationAssignedEvent` is already handled + filtered by the client, but it carries **no `ContactId`** (digital hydrates contact by fetching the conv) and its client handler hardcodes `contactId=''` → for voice the contact panel/history would stay empty unless we add a fetch. A **dedicated `VoiceScreenPopEvent(TenantId, ConversationId, AgentId, Channel, ContactId, ContactName, CallerNumber, VoiceLinkedId)`** carries everything the screen-pop needs (contactId → contact/history hydrate; callerNumber → call-card), doesn't conflate with digital-assign semantics, and plugs into the same `isForCurrentAgent` filter. Cost: one new record + one client handler + one `ApiJsonContext` entry + tests. **Recommendation: new event.**

### D2 — Screen-pop UX: auto-navigate on answer (recommended) vs call-card-click-to-open
Digital has no auto-open (clickable toast). For voice, the agent has answered a live call — true "screen-pop" auto-surfaces the context. **Recommendation: on the softphone transition to Active (answered), imperatively `select(conversationId)+navigate('/agent/conversation/{id}')`** (mirror `inbox-item` navigation). The floating call card stays (timer/hangup) and becomes conversation-aware. Alternative: no auto-nav — the call card shows the contact + an "Open conversation" button. (Auto-nav must not destroy unsaved composer state on another conversation — acceptable for voice answer.)

### D3 — Agent-assist (transcript/sentiment) — defer (recommended) vs include now
Per §2 Non-goals: defer. The screen-pop ships contact + history + disposition (no live transcript). **Recommendation: defer** (STT is a lab stub; the binding is a meaty refactor). Including it now expands 3B.1 by the per-conversation `agent-ai-store` refactor + backend `ConversationId` enrichment + the empty-`CallSession.AgentId` fix.

---

## 4. Design (assuming D1=new event, D2=auto-nav, D3=defer)

### 4.1 Backend
- New `sealed record VoiceScreenPopEvent(string TenantId, string ConversationId, string AgentId, string Channel, string ContactId, string ContactName, string CallerNumber, string VoiceLinkedId) : PlatformEvent(TenantId, "voice.screenpop", _clock.UtcNow)` in `PlatformEventBus.cs` (NO `Metadata` override → tenant-broadcast, client filters by AgentId). Register in `ApiJsonContext.cs` (SSE block).
- `VoiceConversationBridge`: inject `IContactStore`. In `OnCallConnectedAsync`, inside `if (becameActive)` after the Owner-assign + Save, when `agentId is { } owner`: resolve the contact display name (`IContactStore.GetByIdAsync(tenantId, conversation.ContactId)` → name; fallback `session.CallerIdNum ?? "anonymous"`), then `_eventBus.Publish(new VoiceScreenPopEvent(tenant, conversation.ConversationId.Value, owner.Value, nameof(ChannelType.Voice), conversation.ContactId.Value, contactName, session.CallerIdNum ?? "", session.LinkedId))`. Leader-gated already (the whole handler is). No throw on contact-store failure (best-effort; fall back to caller number).
- Tests: bridge publishes `VoiceScreenPopEvent` with the right AgentId/ConversationId/contact on Active; not published when `agentId` unresolved; not on re-delivery (gated by `becameActive`). Serialization round-trip. **`AgentId` MUST equal `Agent.AgentId`** (the value the client matches) — a regression test asserts it.

### 4.2 Frontend (Web)
- `voice-call-store`: add `associatedConversationId: string | null` + `callerName` + a setter `associateConversation({conversationId, callerName, callerNumber})`; thread through `idle`/`reset`.
- `use-sse.ts`: add `source.addEventListener('voice.screenpop', …)` mirroring the `conversation.offered` block — `JSON.parse`, `if (!isForCurrentAgent(data.agentId, currentAgentId())) return;`, then re-dispatch via `onSseEvent`.
- `conversation-store` (`initConversationSSE`): on `voice.screenpop` → **spread-merge** upsert a voice Conversation (`channel:'voice'`, `state:'active'`, `contactId`, `contactName`, `id=conversationId`) so the panel + ContextPanel (contact/history) + wrap-up can hydrate; never wipe existing fields.
- `voice-call-store`/`use-softphone`: on the screen-pop event set `associatedConversationId` + caller fields; on softphone phase→active, `select(conversationId)+navigate('/agent/conversation/{id}')` (auto-nav, D2). Tolerate the SSE event arriving before/after the SIP ring (no `phase==='ringing'` assumption).
- `conversation-panel`: add a `channel==='voice'` branch — suppress `MessageThread` + `ReplyComposer`; show contact + history (ContextPanel already does this off `contactId`) + the call-card controls. Header contact/badge as today.
- `call-card`: show `callerName`/`callerNumber` from the store (3A empty); an "Open conversation" affordance when `associatedConversationId` is set.
- Wrap-up: on softphone hangup the voice conv is `wrap_up` (backend) → auto-open `WrapUpDialog` keyed by `associatedConversationId` (a `useEffect` on `state==='wrap_up'`, or the existing manual button). Reuses `useWrapUp` → POST `/conversations/{id}/wrapup`.
- i18n: new `voice.*` screen-pop/wrap-up keys in en-US + es-419 + pt-BR `agent.json` (CI parity).
- Tests (vitest): `use-sse` voice handler dropped when `agentId != my agentId` (mirror the P1 regression); `voice-call-store` association; `conversation-store` voice upsert spread-merge; call-card conversation-aware render. Playwright (optional) deferred to lab E2E.

### 4.3 Out of scope wiring confirmed
No SSE endpoint / `PlatformDeliveryFilter` change (reuse tenant-broadcast + client filter). No new HTTP endpoint. `channel='voice'` already flows through the untyped Web channel string + icon/label maps.

### 4.4 Agent-assist per-conversation binding (D3 = INCLUDE — moved from Non-goals)
The plan's "screen-pop + agent-assist transcript/sentiment" is delivered in full. All Platform + Web (no Pro change — the Pro engine producing suggestions is unchanged; only the Platform-side `AgentAssistBridge` + the Web store/components change).
- **Backend:** add `string ConversationId` to the four `AgentAssist*Event` records in `PlatformEventBus.cs` (keep `SessionId` for back-compat; both registered in `ApiJsonContext`). In `AgentAssistBridge`, on session start resolve the voice Conversation via `IConversationStore.FindByVoiceLinkedIdAsync(tenantId, session.CallSession.LinkedId)` — the SAME `voice_linked_id` join key `VoiceConversationBridge` writes — and stamp `ConversationId` on the emitted events. **Fix the empty-agentId trap:** `AgentAssistBridge` reads `session.CallSession.AgentId` which "is often empty for app_queue"; derive the platform agentId from `session.CallSession.AgentInterface` (`PJSIP/{tenant}-agent-{agentId}`) via a helper shared with `VoiceConversationBridge.ExtractAgentId` (extract to a small internal `AgentInterfaceParser` used by both), so the client `isForCurrentAgent` filter doesn't drop every agentassist event. Digital conversations keep their existing `ConversationId` (already known on the digital path) — confirm the digital agent-assist path still populates it.
- **Frontend:** refactor `agent-ai-store` from a single global session to a **per-conversation keyed** store (`Record<conversationId, {suggestions, sentiment, transcript, complianceAlerts}>`); `use-sse` routes `agentassist.*` by `data.conversationId` into the right slice (still gated by `isForCurrentAgent(data.agentId, …)`); `SuggestionBanner` / `SentimentGauge` / `ComplianceAlert` / `TranscriptTab` take a `conversationId` prop and read their slice (so a voice conversation and a concurrent digital conversation don't bleed). Clear a conversation's slice on its call/conversation end. The `conversation-panel` voice branch (§4.2) renders these for the voice `conversationId`.
- **Honest caveat:** STT is a lab stub ([3B.0 spec §9 Risk 5]) → the live transcript shows no real data yet, but the binding is correct + ready for when STT is real. Tests assert the routing/keying (an `agentassist.suggestion` with `conversationId=X` lands in slice X and not in a concurrent slice Y), independent of STT.

---

## 5. Risks
1. **AgentId mismatch (the recurring P1 trap):** the event's `agentId` MUST be `Agent.AgentId` (what `resolveAgentId` reads), not `User.UserId` — else every screen-pop is silently dropped. Locked by a backend test + the client filter test.
2. **upsert wipes fields:** `conversation-store.upsertConversation` replaces the record — the voice handler must spread-merge (P1 blank-inbox lesson).
3. **Event/ring ordering race:** the `voice.screenpop` SSE and the SIP INVITE (`onCallReceived`) are independent async paths — association logic must be order-independent.
4. **camelCase payload:** client reads `data.agentId`/`data.conversationId` (camelCase); a PascalCase read silently yields `undefined` → filter drops it.
5. **Contact-store lookup failure** in the bridge must be best-effort (fall back to caller number; never throw — StopHost).
6. **Auto-nav UX:** auto-navigating on answer yanks the agent's current view — acceptable for an answered call, but confirm (D2).

## 6. Open items for approval
- **D1.** Screen-pop event: new `VoiceScreenPopEvent` (recommended, carries ContactId) vs reuse `ConversationAssignedEvent`.
- **D2.** UX: auto-navigate to the conversation on answer (recommended) vs call-card-click-to-open (no auto-nav).
- **D3.** Agent-assist transcript/sentiment binding: defer to 3B.1b/3B.2 (recommended; STT is a lab stub) vs include now (per-conversation store refactor + backend ConversationId enrichment).

## 7. Verification
- Api.Tests (bridge publishes `VoiceScreenPopEvent`, AgentId correctness, serialization) + Web vitest (handler filter, store association, voice upsert, call-card) green; 0 warnings; AOT-clean; i18n parity. Lab E2E (reuse the 3B.0 + 3A harness: real call → answer in a registered browser softphone → screen-pop the conversation → hangup → wrap-up) — the answer-path lab proof deferred with 3B.0 becomes achievable here once a softphone is registered.

## 8. Review outcome (A+B "screen-pop core", 2026-05-31)

Adversarial multi-dimension review (5 dimensions, cross-repo) → 18 confirmed / 10 refuted. Fixed in the A+B commit:
- **#1 (HIGH) ContextPanel desync:** the voice path navigated but never `select`ed, so the right-side ContextPanel (contact/history/notes, keyed on `selectedId`) showed stale/empty data. Fixed with a route→`selectedId` sync effect in `conversation-view.tsx` — also covers inbox-click + deep-link/refresh uniformly.
- **#3 + #14 (MED/LOW) wrap-up auto-open:** re-fired on revisit after dismissal (state-based), and missed when the agent was away at hangup. Fixed with a store-level one-shot (`voice-call-store.wrapUpPromptedFor`, re-armed on the next call's `incoming`): opens exactly once, and opens once when the agent returns to a just-ended call.
- **#11/#12/#13 (LOW-MED) AgentAssistBridge:** `OnSessionStartedAsync` now subscribes + stores the composite BEFORE the `await` (Rx subjects don't replay → no dropped early emissions; a concurrent `OnSessionEnded` always finds the composite → no orphan); the `conversationId` is assigned post-await into the lazily-read closure; agentId derivation is tenant-guarded (`DeriveAgentId`) so an empty tenant can't collapse it (re-arming the client-drop trap).
- **#16 (LOW) normalizer:** `Escalated` added to terminal server states (was silently dropped).
- **#15 (LOW) call-card:** added the "Open conversation" re-entry affordance (G3).
- Tests: `AgentInterfaceParserTests` + `AgentAssistBridgeTests.DeriveAgentId` (the P1-trap/precedence) + `VoiceScreenPopEvent` camelCase contract assertion + `applyVoiceScreenPop` spread-merge + voice-call-store association/one-shot. (Deferred follow-up: a `conversation-panel` voice-branch render test — the branch is type-checked + its logic is store-tested.)

**Documented decisions (not changed):**
- **#5 (MED) multi-pod SSE delivery:** `voice.screenpop` — like ALL `conversation.*`/`agentassist.*` SSE — is delivered via the in-process `PlatformEventBus` subject, so in multi-pod K8s an agent only receives it if their `/events/stream` connection lands on the publishing (AMI-leader) pod. This is a **pre-existing, systemic** Platform-SSE limitation (not introduced by 3B.1), and **SMB single-host (the product target) is unaffected**. A cross-pod fix (route Platform SSE through the Pro.Push Redis backplane already used by the SignalR Hub) is a separate K8s-hardening item.
- **#6 (MED) caller PII in the broadcast:** `VoiceScreenPopEvent` carries `ContactName` + `CallerNumber` as display hints. The SSE stream is **tenant-isolated** (`PlatformDeliveryFilter`) and within a tenant agents already have contact-DB access, so this is authorized data reaching authorized parties (only the matched agent renders it). The display hint avoids a contact-fetch flash on answer. A future hardening (drop the hints, hydrate by `contactId` after the AgentId filter — the `ConversationAssignedEvent` precedent) is noted but not required; severity reassessed to low (tenant-scoped, authorized).
