# Plan: Phase 3B — Voice as a tracked Conversation + server-orchestrated call control

> Mirror of the approved system-path plan (`~/.claude/plans/recursive-seeking-key.md`). This repo is authoritative. `git mv` to `completed/` on ship.

## Context

Phase 3A shipped + proved (real headless-browser audio E2E) an in-browser SIP.js softphone: an inbound DID call rings the agent's tab, they answer, two-way WebRTC audio. But the call is just a **standalone SIP session** — not a tracked Conversation. For the **final product** (a real omnichannel contact center, not a phone), voice must become a first-class channel: screen-pop (who's calling + history/case), live agent-assist (transcript/sentiment), disposition/wrap-up, CDR, and full **call control** (hold/transfer/conference/consult/record/monitor/outbound). Phase 3B closes that gap.

**Deep-analysis reframe (this changed the plan):** the naive "build a greenfield AMI listener + voice-Conversation tracker, rebuild the softphone on SIP.js multi-session for attended transfer" was wrong twice:

1. **`Web.SimpleUser` is single-session**, so attended transfer/consult/conference *seem* to need a client multi-line rebuild. They do NOT — the correct contact-center architecture is **server-orchestrated**: the browser stays a thin WebRTC media endpoint (SimpleUser), and transfer/conference/consult/record/monitor are **server-side** Asterisk actions (AMI/ARI). Survives refresh, enables supervisor, avoids the multi-line trap.
2. **The server-side call infrastructure already EXISTS + is wired** — would have been reinvented:
   - `VerbaraServer` (Verbara.Sdk.Live) actively consumes AMI events → live model via 5 managers (Channels/Queues/Agents/Bridges/MeetMe). The "AMI listener" exists.
   - `CallSessionManager` (Verbara.Sdk.Sessions) → multi-party `CallSession` (participants/queue/agent/bridge/linkedid) indexed + `IObservable<SessionDomainEvent>`.
   - `WithConversationBridge()` / `WithAgentBridge()` (Program.cs:148-151) bridge `CallSession ↔ Conversation` for inbound + agent state.
   - CDR (`EventStoreSubscriber → SessionCompletionProjector → completed_sessions`) captures inbound voice. AgentAssist (`AgentAssistEngine → AgentAssistSession`: transcript/sentiment/compliance) framework real (STT pluggable).
   - `RealtimeStateBridge` (Program.cs:910) implements presence→pause (Available/Away → `SyncAgentPausedAsync` + AMI `QueuePause`). The 3A E2E "gap" was operational — the agent was created Offline + never set Available.
   - All call-control primitives exist: AMI `BlindTransfer/Atxfer/Bridge/Redirect/Originate/MixMonitor/Confbridge*`; ARI `Channels.{Hold,Unhold,Mute,SendDtmf,Record,Snoop,Redirect,StartMoh}` + `Bridges` + `Recordings`. ARI `Snoop` = supervisor spy/whisper/barge native. Missing only a thin orchestration layer + the Platform voice API + the Web UI.

**Conclusion (no shortcuts, final-product):** 3B = **activate + verify the existing server-side voice pipeline, then expose it to the browser agent** via a thin voice call-control API + UI. Server is the source of truth (AMI/ARI live model); the browser stays a SimpleUser media endpoint; its `voice-call-store` becomes a *projection* of server-pushed state. Huge reuse of shipped infra; correct architecture for transfer/conference/monitor/recording.

## Status

- **3B.0 — ✅ CODE COMPLETE + verified 2026-05-31 (local/unpushed).** Spec: [`docs/specs/2026-05-31-voice-conversation-bridge-3b0.md`](../../specs/2026-05-31-voice-conversation-bridge-3b0.md) (§11 outcome). New `VoiceConversationBridge` (leader-emit, tenant via AMI GetVar fail-closed, idempotent voice Conversation by `LinkedId`, lifecycle Queued→Offered→Active / WrapUp / Abandoned, capacity, ACW) + migration 027 (`voice_linked_id`) + `voice:ami:owner:leader` lease. Empirical findings vs the original plan: (a) the wired `ConversationStatePushBridge` did NOT create a Conversation (built new); (b) `CallSession.TenantId` is null for inbound (resolve via `TENANT_ID` GetVar + stamp); (c) AMI server IS auto-started (ClusterManager), CDR + presence→pause already wired. Adversarial review (15 confirmed findings) hardened idempotency (SDK double-emits `CallConnectedEvent`), failover tenant-recovery (G2), striped locks. **Api.Tests 1107/1107**, 0 warnings, AOT-clean. **Lab E2E core PROVEN** (real SIPp call → `[VOICE-CONV] Created voice Conversation … for tenant acme`, channel=Voice, voice_linked_id=Asterisk LinkedId, Queued→Abandoned; AMI live Q1; leader-emit active). Lab-deferred: Q5 CDR (Pro license-gated in lab) + answer-path (needs softphone; unit-tested).
- **3B.1 / 3B.2 / 3B.3 — pending** (own detailed plans at kickoff).

## Phased scope (ship incrementally — each green before the next). Detail 3B.0 + 3B.1; outline 3B.2 + 3B.3.

### 3B.0 — Activate & verify the live voice-session pipeline (foundation; mostly backend wiring + lab verification)
- **Verify the AMI server is live.** `Program.cs:103-104` (`AddVerbaraMultiServer`+`AddVerbaraSessionsMultiServer`) + cluster `InitialNodes["primary"].Ami` (`Program.cs:832-857`). Is a `VerbaraServer` auto-registered + **started with a live AMI connection**? If NOT (registered-but-dormant), wire a leader-gated start (mirror `StasisInboundConsumer`/`VoiceLeaderResources`): `pool.RegisterAsync("primary", amiOptions)` + `server.StartAsync()`. Reuse cluster/leader infra — no parallel listener.
- **Verify `ConversationBridge` creates a voice `Conversation`** for an inbound `CallSession` + resolves the **real tenant** from the call's `TENANT_ID` (3A trunk `set_var`), not the literal `DefaultTenantId="default-tenant"`.
- **Close presence→pause end-to-end (the 3A gap).** Set agent **Available** → `AgentStateChangedEvent` → `RealtimeStateBridge` → `queue_members.paused=0` + AMI `QueuePause` → `Queue()` dials the browser. Confirm the member's initial paused state on `AddQueueMemberAsync`. Add **ACW** auto-transition on hangup if absent.
- **Wire dormant voice capacity.** Drive `VerbaraCapacitySyncService.HandleVoiceCallStarted/EndedAsync` from `CallSessionManager` `AgentConnect`/`AgentComplete` (or confirm already session-driven).
- **Verify CDR.** Inbound voice writes `completed_sessions`.
- Output: a verified live pipeline (session → conversation → presence-pause → CDR). Mostly verification + small wiring.

### 3B.1 — Voice as a tracked Conversation in the agent UI (screen-pop)
- **Backend:** the voice `Conversation` (from the bridge) is offered/assigned to the answering agent + a **userId-targeted SSE** fires (reuse `conversation.offered/assigned`, or add typed `voice.call.{ringing,answered,ended}` `PlatformEvent` records in `ApiJsonContext` with `Metadata.UserId`). Correlate SIP/AMI channel ↔ `Conversation` ↔ browser session (`CallSession.LinkedId` / `AgentInterface = PJSIP/{tenant}-agent-{agentId}`).
- **Web (thin):** SSE handlers in `use-sse.ts` → `voice-call-store.associatedConversationId` + upsert voice `Conversation` into `conversation-store`. The call card becomes the screen-pop entry (contact/history + already-wired agent-assist transcript/sentiment). Wrap-up/disposition on hangup reuses `wrap-up-dialog.tsx`. Populate `callerId`/`callerNumber` from the Conversation (3A left empty).
- Output: inbound voice = tracked Conversation w/ screen-pop + agent-assist + disposition. Reuses `conversation-panel`, `suggestion-banner`, `sentiment-gauge`, `wrap-up-dialog`.

### 3B.2 (outline) — In-call control
Client basics (SimpleUser `mute/sendDTMF/hold`) + `call-control-panel.tsx` + `dtmf-dialpad.tsx`. Server-orchestrated multi-party: new `VoiceCallEndpoints` (`POST /voice/calls/{id}/{transfer,conference,record,...}`) → AMI/ARI via the live `VerbaraServer` + a thin `ICallControlService` (blind=`Redirect/BlindTransfer`, attended=`Atxfer`/Originate+`Bridge`, conference=ARI `Bridges`/Confbridge, record=`MixMonitor`/ARI). Reuse `transfer-dialog.tsx`.

**Auto-answer / auto-accept (added 2026-05-31 — researched, see [3B.0 spec §the validation] + the inbound-delivery memory):** today the agent answers MANUALLY (the softphone rings, `call-card` "Answer" → `simpleUser.answer()`); only the caller leg is auto-answered by `StasisInboundConsumer` to enter the queue. Industry parity (validated against Amazon Connect "Auto-Accept Call" + Genesys Cloud "Auto Answer"): auto-answer is an **opt-in per-agent (and/or per-queue) setting**, NOT a default — the call connects to the agent's headset automatically, with a short **zip-tone**/whisper so they know a call landed. Scope for 3B.2: (1) a per-agent `AutoAnswer` flag (`agents` column + admin toggle + `/agents/me`); optionally a per-queue default that the agent flag overrides (Genesys cascade model). (2) Client: `useSoftphone`/`softphone-manager` auto-accept the incoming INVITE (`simpleUser.answer()` in `onCallReceived`) when the flag is on, gated on a secure context + mic permission already granted; play a short zip-tone on auto-accept. (3) Keep manual as the default (zero behavior change for existing agents). No server call-control needed — auto-answer is a client softphone behavior driven by the agent setting. Sources: [AWS Connect auto-accept](https://docs.aws.amazon.com/connect/latest/adminguide/enable-auto-accept.html), [Genesys auto-answer](https://help.genesys.cloud/articles/turn-on-auto-answer-for-agents/).

### 3B.3 (outline) — Outbound + supervisor
Agent outbound (`VoiceCallEndpoints` originate → `VerbaraServer.OriginateAsync`, Dialer pattern). Supervisor monitor/whisper/barge (ARI `Channels.SnoopAsync`), license-gated.

## Critical files
- Backend: `Program.cs` (multi-server + bridges + cluster InitialNodes + a possible leader-gated start), the `ConversationBridge` (T27), `RealtimeStateBridge.cs` (exists), `VerbaraCapacitySyncService.cs` (dormant), `StasisInboundConsumer.cs`/`VoiceLeaderResources.cs` (pattern).
- SDK (reuse): `Verbara.Sdk.Live/Server/VerbaraServer.cs`, `Verbara.Sdk.Sessions/Manager/CallSessionManager.cs`, `Verbara.Sdk.Ami/Actions/*`, `Verbara.Sdk.Ari`, `Verbara.Sdk.Pro.EventStore`, `Verbara.Sdk.Pro.AgentAssist`.
- Web (thin): `src/core/voice/softphone-manager.ts`, `src/agent/stores/voice-call-store.ts`, `src/agent/voice/call-card.tsx`, `src/core/hooks/use-sse.ts`, `conversation-store.ts`+`agent-ai-store.ts`, reuse `transfer-dialog.tsx`/`wrap-up-dialog.tsx`; new `call-control-panel.tsx`+`dtmf-dialpad.tsx` (3B.2). i18n `voice.*` ×3.

## Process / constraints
Subagent-Driven + FCM, TDD-first, 0-warning, AOT (new DTOs/events in `ApiJsonContext`; `[LoggerMessage]`), Dapper banned, Conventional Commits no Co-Authored-By, Web base-ui/Tailwind4/i18n-parity/`data-*`. Each sub-phase green before next; user pushes manually. SDK gaps bump Pro (2.7.2→2.7.3-pro) only on Pro-surface change; prefer Platform wiring.

## Risks
1. **Liveness (linchpin):** is the "primary" AMI server auto-connected? 3B.0 resolves empirically; if dormant, a leader-gated start is a small add.
2. **Tenant resolution:** the bridge must use the call's `TENANT_ID`, not `DefaultTenantId`.
3. **Two control planes:** server (AMI/ARI) is source of truth; the browser store is a projection (avoid split-brain — server transfer reflects via SSE).
4. **ARI-vs-AMI:** the queued call left Stasis (`ContinueAsync`→`Queue()`) → AMI-controlled; ARI for richer ops on separate channels (Snoop/Record). Don't ARI-control a left-Stasis channel.
5. **AgentAssist STT** may be a lab stub; screen-pop works without live transcript.
6. **Leader-gating:** single-owner live AMI server per node (mirror Stasis gate) to avoid duplicate session/CDR.

## Verification (3B.0 + 3B.1, lab)
Reuse the 3A harness (`/tmp/sipharness/`, host-run Api, cert'd asterisk, the WebRTC agent).
1. Confirm a live "primary" AMI server (logs) + a `CallSession` forms on inbound.
2. Presence→pause: set agent Available via API → `paused=0` + `Queue()` dials (the 3A gap closed properly, no manual SQL).
3. SIPp inbound → browser rings → assert a voice `Conversation` (channel=Voice, correct tenant) + a `conversation.offered`/`voice.call.*` SSE reaches the agent.
4. Answer → Conversation→Active + call card screen-pops the Conversation.
5. Hangup → wrap-up/disposition + a `completed_sessions` CDR.
6. Tests: new events/DTOs `[JsonSerializable]` + serialization test; Api.Tests for wiring; Web vitest for SSE handlers + screen-pop. Suites green.

3B.2 + 3B.3 get their own detailed plans at kickoff.
