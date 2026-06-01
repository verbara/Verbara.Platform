# Plan 3B.2 — Voice call control + auto-answer + blind transfer + outbound click-to-dial

## Context

Phase 3B.0 made an inbound voice call a tracked `Conversation` (AMI→bridge, leader-emit, idempotent by Asterisk `LinkedId`). Phase 3B.1 added screen-pop + per-conversation agent-assist. The agent can now **answer/reject/hangup** an inbound call in the browser softphone — but that's it. For a real omnichannel contact center the agent needs full **call control** on a live call (hold, mute, DTMF, transfer), an **auto-answer** option (industry-standard opt-in), and the ability to place **outbound** calls. 3B.2 closes that gap; attended/consult transfer, conference and supervisor monitor are deferred to 3B.3.

The softphone is SIP.js `Web.SimpleUser` (single-session, one call at a time). The architecture invariant: **the browser stays a thin media endpoint.** Pure-media ops SimpleUser already exposes (`hold/unhold/mute/unmute/sendDTMF`) are client-side; anything touching another channel or the PBX (blind transfer, outbound origination) is **server-orchestrated AMI**, leader-gated through the existing `voice:ami:owner:leader` lease, and reflected back to the browser as a projection. The store is never the source of truth for server-side state.

## Scope (user-decided)

- **Core control:** hold/unhold, mute/unmute, DTMF dialpad — client-side (SimpleUser).
- **Auto-answer:** per-agent flag **+ per-queue default** the agent overrides (tri-state cascade). Auto-accept INVITE + zip-tone; manual stays default.
- **Blind transfer** to queue / another agent / external number — server-side AMI `Redirect`, leader-gated. **Attended/consult deferred to 3B.3.**
- **Outbound click-to-dial** — server-originate **reusing the Pro Dialer stack** (NOT browser `SimpleUser.call`, which bypasses caller-ID/trunk/DNC/tracking). Tracked as an outbound `Conversation`.

Deferred to 3B.3: attended/consult/conference, supervisor monitor/whisper/barge, recording controls.

## Status

- **3B.2a — ✅ SHIPPED 2026-06-01 (Web `4994954`, local/unpushed).** Client hold/mute/DTMF via SimpleUser + store mirrors + call-card control row + dtmf-dialpad + i18n ×3. vitest 1080→1097, 0 warnings, tsc/eslint/i18n clean. Lab audio E2E (hold stops far-end RTP, DTMF received) bundled with the 3B.2b lab session.
- **3B.2b — ✅ SHIPPED 2026-06-01 (Platform `1bcd5092` + Web `2f8a955`, local/unpushed).** Auto-answer cascade (per-agent tri-state `bool?` override ?? per-queue `AutoAnswerDefault`). Backend: model + migration 028 + Postgres stores + `AgentMeResponseDto` + admin agent/queue create/update (UpdateAgent sets AutoAnswer unconditionally so the UI can reset to Inherit) + `VoiceScreenPopEvent` `QueueName`/`QueueAutoAnswerDefault` + bridge resolve (best-effort). Web: `isAutoAnswerEffective` + `autoAnswerCall` (secure-context + granted-mic gate + zip-tone + skip-toast), `use-softphone` one-shot effect, store `queueAutoAnswerDefault`/`autoAnswered`, agent-form tri-state Select + queue-form/queue-detail/queues-page checkbox + i18n ×3. **Also fixed a latent production bug**: `PostgresAgentStore.GetByUserIdAsync` (the only caller is self-scoped `/agents/me`, which 3A made expose the SIP secret) OMITTED `extension`/`sip_password` from its SELECT → the softphone got a **null sipPassword on Postgres** (InMemory tests masked it). All SELECTs now project them; `[JsonIgnore]` still guards accidental leaks. Locked by a Testcontainers Postgres IT. Api.Tests 1119, Postgres IT 5/5, Web vitest 1097→1106, 0 warnings, tsc/eslint/build/i18n clean.
- **3B.2c — 🚧 BACKEND SHIPPED 2026-06-01 (Platform `4048782b`, local/unpushed); Web pending.** Blind transfer (queue/agent) of the live call: bridge stamps `Metadata["customerChannel"]` at connect (failover-safe); leader-gated `VoiceCallControlService` resolves the target, Setvars the context var, and AMI-`Redirect`s the customer leg (Queue → `[stasis-queue]`, Agent → new `[transfer-agent]` dialplan); `POST /conversations/{id}/voice-transfer` with owner-assert; DTOs in `ApiJsonContext`; gated on voice-AMI. **External-number transfer deferred to 3B.2d** (reuses the outbound route→trunk resolution built there). Api.Tests 1119→1124, 0 warnings, AOT-clean. **Web pending**: `voice-transfer-dialog` (queue/agent picker) + `useVoiceTransfer` hook + call-card transfer button + i18n.
- **3B.2d — pending.** Lab audio E2E (3B.2a hold/DTMF + 3B.2b auto-answer + 3B.2c transfer) consolidated with the 3B.2c-web/3B.2d lab session.

## Sub-phases (ascending coupling/risk; each green + lab-E2E before the next, mirroring 3B.0/3B.1)

### 3B.2a — Core client call control (hold/mute/DTMF)
Pure-client, no backend, no DTOs, no AMI. Establishes the call-card control surface the later sub-phases reuse.
- `src/core/voice/softphone-manager.ts` — add guarded wrappers `holdCall/unholdCall/isHeld`, `muteCall/unmuteCall/isMuted`, `sendDtmf(tone)` delegating to the confirmed SimpleUser methods.
- `src/agent/stores/voice-call-store.ts` — add `isHeld`/`isMuted` UI mirrors (SimpleUser is truth) + setters; re-armed to false on `incoming`/`reset`. **Hold is a sub-state of `active`, NOT a new phase** (keep the `idle|ringing|active|ended` machine + 3B.1 wrap-up/association untouched). Mute and hold are independent — mirror both separately.
- `src/agent/voice/call-card.tsx` — in-call control row (active phase only) using the `data-testid="voice-{action}-btn"` convention.
- New `src/agent/voice/dtmf-dialpad.tsx` — `0-9 * #` grid → `sendDtmf`; `data-testid="voice-dtmf-{tone}"`.
- i18n: `voice.hold/unhold/mute/unmute/dialpad` ×3 (CI-blocking parity).

### 3B.2b — Auto-answer (per-agent + per-queue default cascade)
Backend data + DTO + one client hook; no AMI. Must precede transfer/outbound because the **queue name on the screen-pop** (added here) is a shared prerequisite.
- Backend model: `Verbara.Platform.Queues/Agent.cs` → add `bool? AutoAnswer` (**nullable tri-state**: unset cascades to queue default). `Verbara.Platform.Queues/Queue.cs` → add `bool AutoAnswerDefault`.
- DTO: `AgentMeResponseDto` (+`FromAgent`) surfaces `AutoAnswer`; `AdminEndpoints` agent create/update + the queue create/update endpoint set the new fields.
- Persistence: `PostgresAgentStore` + Postgres queue store map the new columns (mirror the 3A `extension`/`sip_password` handling, but always-projected; NULL distinct from false for the agent tri-state). New migration `Migrations/028_VoiceAutoAnswer.sql` (`agents.auto_answer boolean NULL`, `queues.auto_answer_default boolean NOT NULL DEFAULT false`).
- **Queue name onto screen-pop (linchpin):** `VoiceConversationBridge` stamps `Metadata["queueName"]` (stripped `{tenant}-` prefix from `CallSession.QueueName`) at connect; extend `VoiceScreenPopEvent` (in `PlatformEventBus.cs`, already in `ApiJsonContext`) with `QueueName` + `QueueAutoAnswerDefault` (bridge resolves the `Queue` by name, fail-open false).
- Web: `use-agents.ts` Agent type + admin payloads add `autoAnswer`; `agent-form.tsx` + the queue form add toggles (`data-testid="agent-auto-answer"`). `use-sse.ts` `voice.screenpop` carries the queue fields into `voice-call-store.associateConversation`. The **auto-answer decision** lives in `softphone-manager.ts` `onCallReceived` (today `incoming('','')`): compute effective = `agent.autoAnswer ?? queueAutoAnswerDefault`; if true, gate on `window.isSecureContext` + a granted mic (`navigator.permissions.query({name:'microphone'})`), play a short WebAudio **zip-tone** into the local monitor (not the remote leg), then `answerCall()`. Never auto-answer into a denied mic → fall back to manual + toast.
- i18n: `voice.auto_answer*`, `admin.agents.autoAnswer`, queue `autoAnswerDefault` ×3.

### 3B.2c — Blind transfer (queue / agent / external)
Server-side AMI `Redirect`, leader-gated. Needs 3B.1 `associatedConversationId`→`VoiceLinkedId` + 3B.2b queue name.
- **Channel correlation:** `VoiceConversationBridge` persists the **customer leg** channel at connect via `Metadata["customerChannel"]` (`session.Participants` where Role=Caller — the trunk leg, NOT the agent leg). Transfer redirects THAT channel. Fallback to `CoreShowChannelsAction` filtered by `LinkedId` only if metadata absent (failover edge).
- New `IVoiceCallControlService`/`VoiceCallControlService` — leader-gated (`[FromKeyedServices(VoiceLeaderResources.AmiOwner)] IClusterLeader`, `if(!_leader.IsLeader) return Failed`). `BlindTransferAsync(tenant, conversationId, target)` issues `RedirectAction` on the customer channel. **Use `RedirectAction`, not `BlindTransferAction`** — the customer leg is in `Queue()` off-Stasis; Redirect re-enters dialplan deterministically. Per-target matrix: **Queue** → set `QUEUE_NAME={tenant}-{queue}`, `Context=[stasis-queue]`, `Exten=s`; **Agent** → small `[transfer-agent]` context dialing `PJSIP/{tenant}-agent-{targetId}`; **External** → `[outbound-agent]` context (shared with 3B.2d, route/caller-ID resolved).
- New `Verbara.Platform.Api/Endpoints/VoiceEndpoints.cs` — `POST /api/v1/conversations/{id}/voice-transfer` (`VoiceTransferRequest{TargetKind,Target}` → `VoiceTransferResponse{Accepted,Error}`); resolve caller agent via the `AgentEndpoints` `GetCurrentUserId`→`GetByUserIdAsync` pattern + assert conversation ownership. DTOs + `TargetKind` enum in `ApiJsonContext`. Register service + `MapVoiceEndpoints` in `Program.cs` gated on `voiceAmiEnabled`.
- Web: new `src/agent/voice/voice-transfer-dialog.tsx` (3-mode picker; do NOT reuse the digital `TransferDialog`) + `useVoiceTransfer()` hook + a `voice-transfer-btn` on the call-card. On success the agent leg drops → existing `onCallHangup`→`ended()`→3B.1 wrap-up. i18n `voice.transfer*` ×3.

### 3B.2d — Outbound click-to-dial (server-originate, reuse Dialer stack)
Highest risk (new endpoint + Conversation direction + browser correlation). Reuses 3B.2c's `VoiceEndpoints` + `[outbound-agent]`.
- New thin `IAgentOutboundDialService`/`AgentOutboundDialService` (NOT `DialerEngine`, which is campaign-driven). Composes Pro resolvers directly: tenant+agent auth → `DncCheckerBase.IsBlockedAsync(checkGlobal:true)` (reject if blocked) → `OutboundRouteResolverBase.ResolveAsync(campaignId:null)` → trunk → caller-ID (see resolution below) → `OriginateAction` (channel = the **agent endpoint** `PJSIP/{tenant}-agent-{id}` as the A-leg so the softphone rings; `Context=[outbound-agent]`, `Exten=number`, `IsAsync=true`, `SetVariable` `TENANT_ID`/`AGENT_ID`/`VERBARA_OUTBOUND_ID={correlationId}`) → `OriginateExecutorBase.ExecuteAsync(action,"primary")` (circuit-breaker + trunk-health).
- `POST /api/v1/voice/dial` (`VoiceDialRequest{ToNumber?,ContactId?}` → `VoiceDialResponse{Accepted,CorrelationId,Error}`) in `VoiceEndpoints.cs`; DTOs in `ApiJsonContext`. Returns `correlationId` immediately for optimistic client state.
- **Outbound as a tracked Conversation:** `AgentOutboundDialService` pre-creates the voice Conversation (Owner=agent, `Metadata["direction"]="outbound"`); `VoiceConversationBridge` (extend its current inbound-only short-circuit) reads `VERBARA_OUTBOUND_ID` on the outbound `CallConnectedEvent` (AMI GetVar), links the pre-created Conversation, stamps `VoiceLinkedId`, and fires the screen-pop carrying `correlationId`. Direction via metadata (no new column; `Conversation` has no Direction property, `MessageDirection` is unrelated).
- Dialplan `docker/asterisk-config/extensions.conf` — add `[outbound-agent]` (`exten => _X.,1,Dial(PJSIP/${TRUNK}/${EXTEN})`, trunk from the resolved route via channel var). (Existing: `[from-trunk]`→Stasis inbound, `[stasis-queue]`.)
- Web: `contact-info.tsx` Call button (`data-testid="contact-call-btn"`) + `useVoiceDial()`; `voice-call-store` gains `direction` + `pendingDial{number,correlationId}`; `softphone-manager.ts` `onCallReceived` branches — if `pendingDial` exists render "Dialing X" + auto-answer the agent leg (agent initiated). `call-card.tsx` dialing variant. i18n `voice.dialing/call_contact` ×3.

## Key design resolutions

- **Auto-answer cascade:** per-queue default on `Queue.AutoAnswerDefault`; per-agent override as **nullable** `Agent.AutoAnswer` (unset → cascade). Computed **client-side** in `onCallReceived` from `useAgentMe().autoAnswer` + the screen-pop's `queueAutoAnswerDefault` (both inputs already reach the client). Gated on secure-context + granted mic.
- **Transfer correlation:** persisted `Metadata["customerChannel"]` (single source, failover-safe), Redirect the customer leg, leader-gated, `RedirectAction` over `BlindTransferAction` for off-Stasis `Queue()` legs.
- **Outbound correlation:** optimistic `pendingDial` set before the dial POST resolves + authoritative `VERBARA_OUTBOUND_ID` server var as tiebreaker; bridge links the pre-created Conversation.
- **Caller-ID for click-to-dial (no shortcut):** there is **no tenant-level outbound caller-ID today** and `CallerIdResolverBase` is campaign-coupled. Add a **tenant-level outbound caller-ID setting** (small new field on tenant settings) as the source for agent dial; if a Dialer `CallerIdPool` exists, prefer it. Fail-open to the trunk default with a logged warning.
- **DI gap (verified):** `OriginateExecutorBase` is consumed-but-not-registered by `AddProDialer`, and dialer resolvers/DNC register only when a Dialer/Postgres connection string is present. For voice-only tenants, **register `DefaultOriginateExecutor` explicitly gated on `voiceAmiEnabled`** and resolve `OutboundRouteResolverBase`/`DncCheckerBase` as **optional** (`GetService`, fail-open + tenant-default trunk) so click-to-dial works without the full dialer stack.
- **Presence→queue-pause:** ALREADY wired (`RealtimeStateBridge`: Available/Busy routable → `SyncAgentPausedAsync(false)` + `QueuePauseAction`). The 3A "gap" was operational (agent stayed Offline). 3B.2 adds **verification only** — do NOT auto-flip presence on SIP register (conflates registration with intent). Confirm the realtime `AddQueueMember` initial-paused flips to 0 on Available; add code only if the lab shows a residual gap.
- **AOT:** every new DTO/enum in `ApiJsonContext` (+ round-trip tests); `[LoggerMessage]` logging; new services are request-driven leader-gated singletons gated on `voiceAmiEnabled` (no new HostedService — the outbound tracking rides the existing `VoiceConversationBridge`).

## Reused components (paths)

- Leader-gating: `Services/VoiceLeaderResources.cs` (`AmiOwner`) + the `IClusterLeader`/`if(!_leader.IsLeader)` pattern from `Services/VoiceConversationBridge.cs`.
- AMI send: `VerbaraServerPool.GetServer("primary").Connection.SendActionAsync<T>(...)` (`RealtimeStateBridge`/`VoiceConversationBridge` pattern). Actions `RedirectAction`/`OriginateAction`(+`SetVariable`)/`CoreShowChannelsAction` from `Verbara.Sdk.Ami.Actions`.
- Outbound stack (`Verbara.Sdk.Pro.Dialer`): `DefaultOriginateExecutor`, `OutboundRouteResolverBase`/`PostgresOutboundRouteResolver`, `CallerIdResolverBase`, `DncCheckerBase`/`PostgresDncChecker`.
- Correlation: `IConversationStore.FindByVoiceLinkedIdAsync` + `Conversation.VoiceLinkedId`/`Metadata`; `CallSession.Participants`/`QueueName`/`AgentInterface`; `Services/AgentInterfaceParser.cs`.
- Web: `softphone-manager.ts`, `voice-call-store.ts`, `call-card.tsx`, `use-softphone.ts`, `use-sse.ts` (`voice.screenpop`), `use-agents.ts`, `agent-form.tsx`; reuse the 3B.1 wrap-up flow on transfer/outbound hangup.

## Risks

1. Outbound A-leg/B-leg correlation race → optimistic `pendingDial` set before await + `VERBARA_OUTBOUND_ID` tiebreaker.
2. Click-to-dial caller-ID (no tenant setting today) → add the tenant outbound caller-ID field; fail-open to trunk default.
3. `Redirect` vs `BlindTransfer` on off-Stasis `Queue()` legs → Redirect; validate re-queue + agent-target in lab.
4. DI availability for voice-only tenants → explicit registration under `voiceAmiEnabled` + optional resolvers.
5. Auto-answer mic-permission edge → never auto-accept without granted mic in a secure context.
6. Agent tri-state nullable flag → NULL-distinct-from-false in the Postgres mapping + DTO.

## Verification

Per sub-phase: TDD first (xunit/FluentAssertions/NSubstitute backend; vitest Web), **0 warnings**, AOT-clean (`-p:PublishAot=true` publish 0 IL2026/IL3050/IL207x), i18n parity ×3.
- **3B.2a:** vitest store actions + softphone wrappers (mock SimpleUser, assert delegation + no-softphone guard). Lab: headless sip.js agent answers SIPp call → hold stops far-end RTP, DTMF received by the SIPp UAS.
- **3B.2b:** Api.Tests agent/queue column round-trip + migration idempotency + bridge publishes queue fields + DTO serialization. vitest effective-setting + `onCallReceived` gate (auto-accept only when effective && mic granted). Lab: `auto_answer=true` → headless agent auto-accepts (no click) + zip-tone; queue-default-overridden matrix.
- **3B.2c:** Api.Tests service (assert `RedirectAction` channel+context+exten per target, leader short-circuit, ownership reject, missing-channel fallback) + serialization. Lab: SIPp inbound → answer → voice-transfer to a 2nd queue / agent / external → customer leg re-routes, agent leg drops, wrap-up fires.
- **3B.2d:** Api.Tests dial service (DNC-block reject, route resolution, originate action shape, DI-absent fail-open) + serialization. Lab: agent clicks Call → AMI Originate with resolved trunk/caller-ID → headless agent A-leg shows "Dialing" → SIPp UAS answers B-leg → outbound Conversation created `direction=outbound` → wrap-up on hangup; DNC-blocked number rejected pre-originate.

**Lab E2E pattern (all sub-phases):** host-run Api (`dotnet run -c Release`, Staging, `TZ=UTC`) + dockerized reference-smb Asterisk (host-network, ARI/AMI/SIP) + verbara PG; SIPp scenarios + the headless sip.js WebRTC agent harness (`/tmp/sipharness`, esbuild-bundled). New SIPp scenarios: outbound B-leg UAS (3B.2d), re-queue-after-redirect (3B.2c).

## Cross-repo / versioning

- Mostly Platform + Web. **SDK Ami actions already exist** (Redirect/Originate) — no SDK change expected. **Pro Dialer resolvers reused as-is** — likely no Pro change (confirm `OriginateExecutorBase`/resolver visibility for Platform consumption at impl step 1; if a Pro `internal`→`public` tweak is needed, bump Pro 2.7.2-pro → 2.7.3-pro, pack to local feed, and `publish-packages.yml` dispatch as done this session).
- New migration `028_VoiceAutoAnswer.sql`. New tenant outbound caller-ID setting (small migration or settings field).
- Releases remain **deferred** per the 2026-05-25 pivot (gated on first paying customer); 3B.2 validated with local lab images.
- On ship: `git mv docs/plans/active/<this>.md docs/plans/completed/`, update the epic memory.
