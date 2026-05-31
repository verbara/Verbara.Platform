# Spec — Phase 3B.0: Voice-session pipeline activation + Voice→Conversation bridge

**Status:** Approved (2026-05-31 — O1=`Queued`, O2=capacity in the bridge, O3=decide at impl step 1, O4=dedicated `voice:ami:owner` lease)
**Date:** 2026-05-31
**Scope:** Backend (`Verbara.Platform.Api`, `Verbara.Platform.Conversations`, `Verbara.Platform.Storage.Postgres`); lab verification. No Web changes (that is 3B.1).
**Related:**
- Plan: [`docs/plans/active/2026-05-31-voice-tracked-conversation-3b.md`](../plans/active/2026-05-31-voice-tracked-conversation-3b.md) (sub-phase 3B.0)
- Epic memory: [`project_inbound_delivery_epic`](../../../.claude/projects/-media-Data-Source-Verbara-Verbara-Platform/memory/project_inbound_delivery_epic.md)
- Phase 2 blueprint: [`reference_phase2_voice_blueprint`](../../../.claude/projects/-media-Data-Source-Verbara-Verbara-Platform/memory/reference_phase2_voice_blueprint.md)
- Predecessor leader-gate pattern: [`StasisInboundConsumer`](../../src/Verbara.Platform.Api/Services/StasisInboundConsumer.cs) + [`VoiceLeaderResources`](../../src/Verbara.Platform.Api/Services/VoiceLeaderResources.cs)
- ADR to be filed by this work: **ADR-00XX — Single-owner of the live AMI side-effect plane** (covers the latent multi-pod duplicate-CDR; see §8)

---

## 1. Context

Phase 2 delivered inbound voice → Asterisk **queue** (`StasisInboundConsumer` routes the call into `Queue(${QUEUE_NAME})`, proven by SIPp E2E). Phase 3A delivered an in-browser WebRTC softphone that **rings, answers, and bridges audio** (proven headless). But a ringing call today is a **standalone SIP session** — it is NOT a tracked `Conversation`. For the final product (omnichannel contact center, not a phone) voice must be a first-class channel: screen-pop, agent-assist, disposition/wrap-up, CDR. Phase 3B closes that gap; **3B.0** is the foundation: activate + verify the server-side voice pipeline and create the voice `Conversation`.

### 1.1 Empirical state (verified this session, file:line)

A no-shortcuts code investigation of the five pipeline points produced:

| # | Point | Verdict | Evidence |
|---|---|---|---|
| Q1 | AMI "primary" server auto-started, live | ✅ **Wired** | `ClusterManager` (HostedService) `StartAsync`→`ConnectInitialNodesAsync`→`ConnectNodeAsync`→`server.StartAsync` ([ClusterManager.cs](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Cluster/ClusterManager.cs):82,97,300,324); `InitialNodes["primary"]` from `Asterisk:Ami` ([Program.cs](../../src/Verbara.Platform.Api/Program.cs):822-859). **Verify-only in lab.** |
| Q5 | CDR writes `completed_sessions` | ✅ **Wired** | `EventStoreSubscriber` (HostedService) subscribes `CallSessionManager.Events` → `SessionCompletionProjector` → `completed_sessions` (Program.cs:952). **Verify-only.** |
| Q3 | Presence Available → unpause realtime queue_member | ✅ **Mostly wired** | `RealtimeStateBridge` (Program.cs:910): `SyncAgentPausedAsync` (realtime DB) + AMI `QueuePauseAction`. The "3A gap" was **operational** (the agent was created Offline and never set Available). **Open:** confirm `paused` default on `AddQueueMemberAsync`; **no ACW** auto-transition on hangup. |
| Q2 | A voice `Conversation` is created for an inbound call | ❌ **NOT wired — new work** | The wired `WithConversationBridge()` is `ConversationStatePushBridge`, which only **publishes state to SignalR**; it never creates a `Conversation` entity. |
| Q4 | `VerbaraCapacitySyncService` driven by call events | ❌ **Registered but dormant** | `HandleVoiceCallStarted/EndedAsync` exist but are never invoked from `CallSessionManager`; the service only listens for `AgentCapacityChangedEvent`. |

### 1.2 Two material deltas from the plan (must be recorded)

1. **The plan assumed `ConversationBridge` already creates a voice `Conversation`. It does not.** Building a new `VoiceConversationBridge` is 3B.0's primary deliverable, not a verification.
2. **The plan assumed the tenant comes from the `CallSession`. `CallSession.TenantId` is `null` for inbound.** `CallSessionManager` never sets it (grep empty); the doc-comment's "Set by `ITenantResolver` on call arrival" is aspirational — no session-tenant resolver is wired (the only resolver, `CachedAgentTenantResolver`, is for **agent auth/RBAC**). `DefaultTenantId="default-tenant"` (Program.cs:150) is a dev fallback. **The tenant must be read from the `TENANT_ID` channel variable** — the same contract Phase 2.4 established (`ps_endpoints.set_var=TENANT_ID={tenantId}` on the inbound trunk endpoint) — via AMI `GetVarAction` (confirmed present: `GetVarAction`/`GetVarResponse`/`IAmiConnection.SendActionAsync<T>`).

---

## 2. Goals / Non-goals

**Goals (3B.0):**
- G1. Inbound voice produces a tracked **voice `Conversation`** (channel = Voice), with the **real tenant** (fail-closed if unresolved) and the calling contact resolved.
- G2. The pipeline's side-effects are **exactly-once across the cluster** and **lossless across failover** — correct on SMB single-host today and on K8s multi-pod, with a seam that 3B.2/3B.3 commands plug into.
- G3. Dormant voice **capacity sync** is driven by real call lifecycle events.
- G4. Presence→pause is **closed end-to-end without manual SQL** (the 3A gap), with an **ACW** auto-transition on hangup.
- G5. Verify Q1 (live AMI) + Q5 (CDR) in the lab.

**Non-goals (deferred):**
- The agent-UI screen-pop, SSE `voice.call.*` events, and Web handlers → **3B.1**.
- In-call control (mute/DTMF/hold/transfer/conference/record) → **3B.2**.
- Outbound + supervisor monitor/whisper/barge → **3B.3**.
- Routing the existing **CDR projection + capacity through the leader gate** (systemic duplicate-CDR fix) → tracked in the ADR (§8), executed as a separate K8s-hardening change, NOT on 3B.0's create path.

---

## 3. The duplication problem and the chosen architecture

### 3.1 Root cause (systemic, not bridge-specific)

Asterisk AMI is a **broadcast** event stream: every connected client receives every event. `ClusterManager` opens the "primary" AMI connection on **every pod with no leader gate** (ClusterManager.cs:82→324). So in multi-pod K8s, every pod's `CallSessionManager` emits the same `CallSessionManager.Events`, and **every** event-driven side-effect duplicates: CDR (existing), capacity, and the new voice `Conversation`. SMB single-host (the near-term product target) has one pod → no duplication.

### 3.2 Option analysis (the full space; the original three were incomplete)

| Option | Mechanism | Cold-start on failover? | Handles COMMANDS (3B.2)? | Verdict |
|---|---|---|---|---|
| **A** Idempotent-by-`LinkedId` | every pod acts; dedupe the *result* by call key + DB UNIQUE | n/a (lossless) | ❌ can't dedupe a transfer/originate | Necessary but insufficient alone; N× AMI+DB work |
| **B** Leader-gate the *consumer reaction* only | only leader reacts; cold model | ❌ **yes — drops in-flight calls** (AMI has no replay) | ⚠️ partial | **Rejected (trap)** |
| **C** Leader-gate the AMI *connection* | only leader connects the socket | ❌ **yes — failover gap, no consumer at all during relevo** | ✅ | Rejected as primary (gap); revisit as deployment-topology variant |
| **D** Warm-standby, leader-*emit* | every pod connects+builds warm model; only leader emits side-effects/commands | ✅ **no** (next leader is warm) | ✅ | **Chosen (primary)** |
| **F** Dedicated single-replica telephony deployment | extract AMI consumers to their own 1-replica service | n/a | ✅ | Over-engineered for SMB; note as future K8s option |
| — ARI-first | keep call in Stasis, per-call ARI control (no broadcast) | n/a | ✅ | Out of scope — different, much larger architecture; we keep the AMI-live-model decision |

**Key distinctions the original framing missed:** (a) AMI has **no history replay**, so any design where the acting pod's model is *cold* at failover loses in-flight calls (kills B and C); (b) **CREATE** side-effects can be made idempotent but **COMMAND** side-effects (coming in 3B.2/3B.3) cannot — the design must support exactly-once *actions*, so a leader-emit gate is mandatory regardless.

### 3.3 Decision: **D + A**

- **Primary = D (warm-standby, leader-emit).** Every pod's `CallSessionManager` keeps a warm model. A single cluster-wide lease — `voice:ami:owner:leader` (new `VoiceLeaderResources.AmiOwner`, sibling of the existing `.Inbound`) — gates **side-effect emission** in the new bridge and will gate **commands** in 3B.2/3B.3. **On SMB single-host the lease is always held by the only pod → correctness is free, no runtime machinery, but the seam exists.**
- **Safety net = A (idempotent by `LinkedId`).** The voice `Conversation` carries the call's `LinkedId` (Asterisk's call-global id, shared by all channels of one call) as a unique correlation key with a **DB UNIQUE constraint**. Across a failover window (old leader emitted, new leader re-emits the same in-flight call) the second insert is caught and treated as "already tracked" → exactly-once creates, lossless.
- **Systemic CDR fix** routes the existing CDR/capacity through the same `voice:ami:owner` gate — **separate hardening item in the ADR (§8)**, not 3B.0's create path.

**Rationale (product-first, no shortcuts):** the final product is server-orchestrated call control = commands idempotency cannot dedupe. Building the leader-emit seam now (free on SMB, correct on K8s) lets 3B.2/3B.3 plug into an existing gate instead of forcing a rewrite.

---

## 4. Design

### 4.1 `VoiceConversationBridge : BackgroundService` (new, `Verbara.Platform.Api/Services/`)

Subscribes to `ICallSessionManager.Events` (mirrors `EventStoreSubscriber`'s subscription shape). For an **inbound** `CallSession` reaching a tracking-worthy state:

1. **Leader gate (D):** if not `voice:ami:owner` leader, skip emission (warm model still maintained by the always-on `CallSessionManager`). Injected `[FromKeyedServices(VoiceLeaderResources.AmiOwner)] IClusterLeader`. On single-host `IsLeader` is always true.
2. **Tenant resolution (fail-closed):** read the `TENANT_ID` channel variable via AMI `GetVarAction` on the call's channel (the var persists after the call left Stasis into the queue). Resolve the AMI connection via `IServerPool.GetServer("primary").Connection` (same access path `RealtimeStateBridge` uses for `QueuePauseAction`). **No `TENANT_ID` → log + skip; never create a `"default-tenant"` Conversation.**
3. **Contact resolution:** resolve/create the contact from `CallSession.CallerIdNum` via the existing contact store / resolution step (reuse, do not reinvent — same path the inbound message pipeline uses).
4. **Idempotent create (A):** create a `Conversation` with `Channel = Voice`, correlation key = `CallSession.LinkedId`. If a voice Conversation for that `(tenant, LinkedId)` already exists, **no-op** (caught via the DB UNIQUE constraint → treated as success).
5. **Lifecycle:** map session-state transitions to conversation state minimally for 3B.0 (created → active on `Connected`; → wrap-up/closed on `Completed/Hangup`). The richer offer/assign + SSE is **3B.1**.

**Trigger-state choice (open item O1):** create on `Queued` (call entered the queue, before an agent answers — enables 3B.1 "ringing" screen-pop later) vs on `Connected` (agent answered). Leaning **`Queued`** so 3B.1 can screen-pop on ring, but this must be confirmed against how `CallSessionManager` populates `CallerIdNum`/`QueueName` timing.

**StopHost safety:** `HostOptions.BackgroundServiceExceptionBehavior = StopHost` (Program.cs:96-99) — the bridge must never throw out of its event handler (fire-and-forget with an internal guard, mirroring `StasisInboundConsumer.OnEvent`/`PushToHubRelay`), and must no-op cleanly when AMI/ARI is unconfigured (dev/InMemory).

### 4.2 Capacity sync (Q4)

Drive `VerbaraCapacitySyncService.HandleVoiceCallStarted/EndedAsync` from `CallSessionManager` agent-connect/agent-complete events. Two implementation choices (open item O2): (a) subscribe inside `VerbaraCapacitySyncService` itself, or (b) call from the new bridge (single AMI-event subscription). **Leaning (b)** — one subscription, gated by the same leader, avoids a second dormant-service wake-up path. Either way, gated by `voice:ami:owner` (capacity decrement is a side-effect).

### 4.3 Presence→pause close + ACW (Q3)

- Confirm the **initial `paused` state** on `AddQueueMemberAsync` in the realtime engine (Pro). If a member is added `paused=1`, document that presence Available must flip it (which `RealtimeStateBridge` already does) — the lab verification (§7) proves no manual SQL is needed.
- Add an **ACW (After-Call-Work)** auto-transition: on hangup, the agent enters wrap-up (paused) rather than immediately routable, until disposition completes. Confirm whether `RealtimeStateBridge`/agent-state-machine already models ACW; if absent, add a minimal transition. (Disposition UI is 3B.1; 3B.0 only needs the state seam.)

### 4.4 Data model — voice correlation key

The voice `Conversation` needs a `LinkedId` correlation field with a UNIQUE constraint for idempotency. **First implementation step:** inspect the `conversations` schema + `Conversation` entity for a reusable external-reference field (e.g. an existing `external_id`/channel-correlation column or `Metadata`). 
- **If a suitable indexed external-key field exists:** reuse it (value = `voice:{linkedId}`), add a partial UNIQUE index scoped to voice.
- **Else:** new migration `0NN_VoiceConversationLink.sql` (next free ordinal, 3-digit zero-padded) adding `voice_linked_id TEXT NULL` + `CREATE UNIQUE INDEX … ON conversations (tenant_id, voice_linked_id) WHERE voice_linked_id IS NOT NULL`. Class-based row mapping (no Dapper), explicit `NpgsqlDbType` for the nullable param.

### 4.5 AOT / conventions

- Any new event/DTO types → `[JsonSerializable]` in `ApiJsonContext` (`JsonSerializerIsReflectionEnabledByDefault=false`).
- `[LoggerMessage]` source-gen for all logs. No reflection. `TreatWarningsAsErrors`, `WarningLevel 9999`.
- New leader resource constant in `VoiceLeaderResources` (`AmiOwner = "voice:ami:owner:leader"`); wire a second/extended `RegisterLeader` (the builder overload already in use for `.Inbound`), gated on Postgres **AND** `Asterisk:Ami`/`Asterisk:Ari` so only telephony-capable pods win the lease (mirror the existing gate at Program.cs:881-892).
- No Dapper (banned). Npgsql via `Verbara.Sdk.Data.Npgsql`.

---

## 5. Affected files (anticipated)

- **New:** `src/Verbara.Platform.Api/Services/VoiceConversationBridge.cs`; possibly `…/Storage.Postgres/Migrations/0NN_VoiceConversationLink.sql`.
- **Edit:** `VoiceLeaderResources.cs` (+`AmiOwner`); `Program.cs` (register the bridge HostedService + extend leader registration/gating; capacity wiring); `ApiJsonContext` (if new types); the conversation store/entity (correlation field + idempotent create) in `Verbara.Platform.Conversations` + `…Storage.Postgres`/`…Storage.InMemory` (InMemory **must** enforce the same uniqueness or tests pass in-mem and fail on Postgres — the Phase-2.1 lesson).
- **Tests:** `Verbara.Platform.Api.Tests` (bridge wiring, tenant fail-closed, idempotency, leader-gate no-op-when-follower); store-parity tests; serialization tests for any new `[JsonSerializable]` type.

---

## 6. Test plan (TDD-first)

1. **Bridge — tenant fail-closed:** inbound `CallSession` with no `TENANT_ID` → no Conversation created, warning logged.
2. **Bridge — happy path:** `TENANT_ID` present → exactly one voice Conversation (channel=Voice, correct tenant, contact from `CallerIdNum`, `LinkedId` correlation).
3. **Idempotency:** two emissions for the same `(tenant, LinkedId)` → one Conversation (second is a caught no-op). Postgres-level: concurrent insert race resolved by the UNIQUE index.
4. **Leader-gate:** follower pod (`IsLeader=false`) → bridge does not emit; leader does.
5. **Capacity:** agent-connect/-complete drives `HandleVoiceCallStarted/EndedAsync`.
6. **ACW:** hangup → agent enters wrap-up, not immediately routable.
7. **Serialization:** any new `[JsonSerializable]` type round-trips.
8. Suites green: `Api.Tests`, store-parity. 0 warnings. AOT publish clean (no `IL2026`/`IL3050`).

---

## 7. Lab verification (reuse the 3A harness)

Reuse `/tmp/sipharness/` + host-run Api + cert'd Asterisk image + dockerized Postgres (`PG_REALTIME_PORT=5433` on this host). The committed `docker/asterisk-config/*` lab tweaks are reverted/stashed (they are lab-only — host-network PG, lab secrets, cert fallback path).

1. Confirm a live "primary" AMI server in logs + a `CallSession` forms on inbound (**Q1**).
2. Set agent **Available** via API → `queue_members.paused=0` + `Queue()` dials the browser — **no manual SQL** (the 3A gap closed properly) (**Q3**).
3. SIPp inbound → assert exactly one voice `Conversation` (channel=Voice, correct tenant) is created.
4. Answer → conversation → active.
5. Hangup → ACW transition + a `completed_sessions` CDR row (**Q5**).
6. (K8s multi-pod, if a cluster is available) confirm a single Conversation across pods (leader-emit) and no duplicate on a forced leader failover (idempotency net).

---

## 8. ADR to file: single-owner of the live AMI side-effect plane

The investigation surfaced a **pre-existing latent defect**: the un-gated multi-pod AMI connection means CDR (`EventStoreSubscriber`) already duplicates `completed_sessions` rows under multi-pod K8s (invisible in single-pod lab/SMB). 3B.0 introduces the `voice:ami:owner` lease and the warm-standby/leader-emit pattern; the ADR records the decision and tracks routing the **existing** CDR projection + capacity through the same gate as a follow-up K8s-hardening change (the plan's Risk #6). Not on 3B.0's critical path; SMB single-host is unaffected.

---

## 9. Risks

1. **Trigger-state timing (O1):** `CallerIdNum`/`QueueName`/`AgentInterface` may not all be populated at the chosen trigger state — verify against `CallSessionManager` event ordering before fixing the trigger.
2. **Channel-id for AMI Getvar:** the `CallSession` exposes participant channel ids/`LinkedId`; confirm which channel still carries `TENANT_ID` after the call entered the queue (the inbound trunk channel, not the agent leg). Read the var off the **inbound** channel.
3. **InMemory/Postgres parity** on idempotency (Phase-2.1 lesson) — both stores must enforce the same uniqueness.
4. **ACW model** may already exist partially in the agent-state-machine — extend, don't duplicate.
5. **AgentAssist STT** may be a lab stub — out of scope for 3B.0 (screen-pop/transcript is 3B.1).

---

## 10. Open items for approval

- **O1.** Trigger state: create the Conversation on `Queued` (ring-time screen-pop in 3B.1) vs `Connected` (answer). Recommendation: `Queued`, pending event-ordering verification.
- **O2.** Capacity wiring location: inside `VerbaraCapacitySyncService` vs the new bridge. Recommendation: the bridge (single subscription).
- **O3.** Correlation field: reuse an existing conversation external-key field vs a new `voice_linked_id` column + partial unique index. Decided at implementation step 1 after schema inspection.
- **O4.** Confirm `voice:ami:owner` should be a **new** lease vs **reusing** `voice:stasis:inbound:leader` (one "voice owner" pod for both ARI-inbound and AMI-side-effects). Recommendation: a dedicated `AmiOwner` lease (AMI capability ≠ ARI capability; a pod could have one and not the other), but a single combined "voice owner" lease is simpler if both are always co-located.

---

## 11. Implementation outcome (shipped 2026-05-31, local/unpushed)

**Status: CODE COMPLETE + verified (unit + adversarial review + AOT) + lab E2E core PROVEN.**

**Lab E2E (§7), 2026-05-31:** host-run 3B.0 Api against the dockerized reference-smb Asterisk (host-net) + verbara PG :5433. Migration 027 auto-applied; `[AMI] Connected …:5038 v22.9.0` (Q1 ✓); `voice:ami:owner` + `voice:stasis:inbound` leadership acquired; ARI Stasis app `verbara` consuming. A real SIPp INVITE (src 127.0.0.2, the trunk's identify match) → DID 18005551234 → `[STASIS] … (tenant acme, DID 18005551234) → queue` → **`[VOICE-CONV] Created voice Conversation … for tenant acme`**: DB row channel=Voice, **tenant=acme** (resolved via AMI GetVar TENANT_ID, NOT default-tenant), `voice_linked_id`=Asterisk LinkedId, contact resolved, state=Abandoned (Queued→Abandoned on hangup, no agent). **Deferred (not 3B.0 regressions):** Q5 CDR — `EventStoreSubscriber` is Pro + **license-gated** ("Revoked"; lab has no license); the answer-path (Connected→Active + capacity + Busy / Ended→WrapUp + Release + ACW + Q3 presence→pause) needs a registered+answering softphone (3A harness) — covered by the 17 unit tests + 5 GetVar-wire branch tests.

Open items resolved (per approval): **O1** = `Queued` (verified: `CallQueuedEvent` carries `QueueName`+`Position`, and `CallerIdNum`/`LinkedId` are already populated by then); **O2** = bridge drives `IAgentCapacityService` directly with the platform agentId (the dormant `VerbaraCapacitySyncService` handlers are extension-keyed — wrong for session data — so they were left untouched, not contorted); **O3** = new `voice_linked_id TEXT` column + partial unique index (migration 027); **O4** = dedicated `voice:ami:owner:leader` lease gated on `Asterisk:Ami:Hostname`.

Shipped: `VoiceConversationBridge` (`IHostedService`), `Conversation.VoiceLinkedId`, `IConversationStore.FindByVoiceLinkedIdAsync` (tenant-scoped + cross-tenant overload), migration 027, `VoiceLeaderResources.AmiOwner`, Program.cs wiring. Tests: **17 bridge + 8 InMemory voice + 7 Postgres voice (Testcontainers)**; suite **Api.Tests 1107/1107**, 0 warnings, native-AOT publish clean (0 IL2026/IL3050/IL207x).

**Adversarial multi-dimension review hardening (15 confirmed findings, 14 fixed):**
- **Idempotency (HIGH):** the SDK emits ≥2 `CallConnectedEvent`s per answered queue call (the agent-connect path is unconditional). Reserve+Busy+persist are now gated on the genuine `Queued→Active` advance (`becameActive`), so a re-delivery is a no-op — no phantom voice-load leak. Release+ACW are symmetrically gated on the call having been Active.
- **Failover losslessness (HIGH, G2):** the hangup handler now recovers the tenant via the cross-tenant `FindByVoiceLinkedIdAcrossTenantsAsync(LinkedId)` when this pod became leader only at hangup (`session.TenantId` unstamped + trunk channel gone). Without it, a leader flap mid-call would leak capacity + leave the agent stuck Busy.
- **Concurrency (MED):** replaced the per-`SessionId` `SemaphoreSlim` dictionary with a fixed **striped lock pool** (64) — no unbounded growth, no dispose/release race, no cleanup; `WaitAsync` moved inside the `try` with an `acquired` guard.
- **Robustness (LOW):** `ExtractAgentId` uses `IsNullOrWhiteSpace` (a malformed agent interface no longer aborts the conversation transition); redundant re-save on no-op re-delivery removed.
- **Anonymous caller (LOW):** withheld-CID calls intentionally share one per-tenant `anonymous` voice Contact (Conversations stay distinct via `voice_linked_id`); per-caller separation deferred to 3B.1. Documented in code.
- **Coverage (HIGH/LOW):** the tenant-resolution AMI `GetVar` wire was extracted to the internal `ResolveTenantFromChannelAsync` and now has 5 branch tests (success+stamp / unset-var / non-Success / exception / no-server); added Connected-redelivery, failover-recovery, conversation-missing, malformed-interface, and `ConversationStateChanged`-payload tests.
- **#11 (LOW, not fixed — unreachable):** InMemory vs Postgres `voice_linked_id` divergence on a same-`conversation_id` re-save with a *different* `VoiceLinkedId` is impossible in production (`VoiceLinkedId` is `init`-only and the bridge always re-saves the same instance).
