# Channel inbound-delivery gaps — WebChat + Voice (product-readiness probe)

**Date:** 2026-05-30 · **Method:** deep code probe (2 specialist agents — Platform + Asterisk) triggered while reconstructing manuales 04/06 against the real API. Every claim carries a `file:line`. **Severity:** strategic — affects the SMB "fit-for-first-paying-customer" claim for the two V1 inbound channels.

## TL;DR

Reconstructing the Fase-1 channel manuales (04-webchat, 06-voz-sip) revealed they document an **aspirational API**. Probing the real product found that **both V1 inbound channels deliver the inbound conversation upstream but the last-mile "reach an agent" wiring is missing or dead-ends**:

| Channel | Upstream (works) | Last-mile (gap) | Usable today | Verdict |
|---|---|---|---|---|
| **WebChat** | session create + message persist (conversation lands in `Queued`) | never assigned to a queue → distribution worker skips it → invisible to agents, no SSE | yes, **with manual supervisor poll+transfer** | needs-small-product-fix-first |
| **Voice** | trunk (registration) + WebRTC agent fully self-service | `Stasis(verbara,inbound,…)` has **no consumer** → inbound DID dead-ends into Hangup() | yes, **with manual `extensions.conf` edit** | needs-small-product-fix-first |

Both are **"yes-with-workaround," not "broken-broken."** Neither is turnkey out of the box.

## WebChat — detail

**Reality:** `WebChatEndpoints.CreateSession` → `DefaultConversationLifecycleService.CreateAsync` hard-codes `State = Queued`, `Owner = null`. Inbound WS/REST messages call only `IInboundMessagePipeline.ProcessAsync` (dedup→contact→conversation→persist) — never `router.RouteAsync`/`switchboard.AssignToQueueAsync`, and publish **no** event on `PlatformEventBus` (unlike `WebhookEndpoints.cs:88-96`).

**Why agents don't see it:** `QueueDistributionWorker.cs:116` does `if (conversation.Owner?.OwnerId is null) continue;` → owner-less convo is permanently skipped. Agent inbox filters `AssignedAgentId == Owner.OwnerId` (`InMemoryConversationStore.cs:24`) → excluded. Visible only via explicit `GET /conversations?state=Queued` + `GET /supervisor/conversations?state=Queued`. No SSE. `POST /conversations/{id}/accept` rejects (state machine forbids Queued→Active; needs Offered first — `ConversationStateMachine.cs:7-9`).

**Honest workaround today:** supervisor polls `GET /api/v1/supervisor/conversations?state=Queued&channel=WebChat` → `POST /api/v1/conversations/{id}/transfer {targetQueueId}` (TransferToQueue allows Queued→Queued, sets `Owner=ForQueue` — `ConversationSwitchboard.cs:152-159`) → then `QueueDistributionWorker` offers it normally.

**Gaps + fix sizing:**

| Gap | Sev | Fix | Entails |
|---|---|---|---|
| Inbound never routes/assigns to a queue | **blocker** | M | Mirror `WebhookEndpoints.cs:104-108` in `WebChatEndpoints` (HandleWebSocket + SendRestMessage): inject `IInboundRouter`+`IConversationSwitchboard`, `RouteAsync` → `AssignToQueueAsync`. |
| No self-service default queue to resolve | **blocker** | M | `ChannelQueueMapping` is empty + appsettings-only; channel-config `defaultQueueId` is inert. Add per-tenant default-queue (TenantSettings/channel-config read + admin field) OR routing-chain fallback to tenant's first active queue. |
| No realtime signal on new convo | major | S | Publish `ConversationStateChanged`+`ConversationMessage` to bus (mirror `WebhookEndpoints.cs:90-96`). `WebChatEndpoints.cs` only. |
| Automation not a viable workaround | major | L | Engine never subscribes to the bus + no Automation REST/UI → can't be wired by an operator. (Native routing fix is far cheaper.) |
| Agent accept broken on Queued | minor | — | Auto-fixes once routing lands (convo becomes Offered → Accept legal). |

**Net to turnkey:** M (routing+events in WebChatEndpoints) + M (default-queue resolution). Pure Platform, no Asterisk.

## Voice — detail

**Self-service (works, no caveats):** Trunk creation `POST /api/v1/admin/trunks` → `RealtimeSyncEngine.SyncTrunkAsync` writes `ps_endpoints`+`ps_auths`+`ps_registrations` (registration trunks fully provisioned). WebRTC agent: `POST/PUT /admin/agents` with `extension`+`sipPassword` → `SyncAgentAsync` writes `ps_endpoints/ps_auths/ps_aors` using the tenant default realtime profile; enable WebRTC via `PUT /admin/realtime/profiles/{id}` (`Webrtc` bool). WSS/ICE/cert pre-baked in reference-smb image.

**Dead-end (gap):** dialplan ships `[from-trunk] exten=_X.,1,Stasis(verbara,inbound,${EXTEN})` (`extensions.conf:2`) but **no process consumes ARI app `verbara` as a call handler** — the AriClient that connects (`Pro.Cluster ClusterManager.cs:313`) is used only for node enrichment; `VerbaraServer` is AMI-only. Grep for `StasisStartEvent`/queue-join in Platform+Pro src = zero. Inbound call enters Stasis, no handler, control returns, next line Hangup() drops it. **Inbound voice never reaches a queue with shipped config.**

**Honest workaround today:** mirror `docker/demo/demo-overrides/extensions.conf:56-61` — hand-edit `[from-trunk]` to `Answer()` + `Queue({tenantId}-{queueName})` per DID (queue name = `{tenantId}-{queueName}`, `RealtimeSyncEngine.cs:145`), then `asterisk -rx 'dialplan reload'`. Everything else (queue, members, agents, WebRTC) is API-provisioned.

**Gaps + fix sizing:**

| Gap | Sev | Fix | Entails |
|---|---|---|---|
| Inbound DID→queue has no consumer (Stasis dead-ends) | **blocker** | M | Hosted ARI Stasis consumer: register app `verbara`, subscribe `StasisStartEvent`, read `args[0]=='inbound'`+EXTEN, look up DID→queue (new Platform-DB mapping + admin CRUD), Answer + enqueue via existing ARI verbs. "Application glue, not new client work." |
| IP-ACL trunk `identify` row never written | major | S | `SyncTrunkAsync` writes endpoint+auth+registration but no `ps_endpoint_id_ips`. Add `UpsertIdentifyAsync` + `MatchHost` field on `CreateTrunkRequest`. (Registration trunks unaffected.) |
| No trunk status via REST | minor | S | `GET /admin/trunks/{id}/status` over existing `TrunkHealthChecker` (AMI PJSipQualify). |
| No WebRTC/ICE/TURN settings endpoint | minor | M | Optional; reference-smb ships sane defaults. Document env vars instead. |

**Net to turnkey:** M (Stasis inbound consumer + DID→queue CRUD — *the* high-value fix) + S (UpsertIdentify). Registration-trunk + WebRTC-agent sections are honestly self-service already.

## Strategic implication

The SMB product's **two V1 inbound channels do not deliver inbound conversations to agents out-of-the-box.** The 2026-05-26 PRR "fit-for-first-paying-customer" sign-off is overstated for **inbound** (outbound/provisioning is fine).

**Combined fix to make both turnkey ≈ 2 medium efforts:**
- WebChat: M (route+events) + M (default queue) — pure Platform.
- Voice: M (Stasis consumer + DID CRUD) + S (identify) — Pro.Realtime/Asterisk side.

The Stasis consumer is "the single thing standing between *every DID needs a hand-edited dialplan line* and *voice works from the wizard*."

**Connection to Living-docs:** the auto Day-1 journey (`auto/.../01-day1-setup-and-webchat`) documents setup+webchat. Regenerating it against v2.6.0 would auto-produce a manual for a channel that doesn't deliver — so the product fix should precede the living-docs regen of that journey.

## Decision pending

Product-vs-docs fork (owner's call): (A) land the ~2 medium fixes → manuales honest as turnkey; (B) document the manual workarounds honestly (undercuts "webchat just works" / "no SIP knowledge required"); (C) hybrid — fix WebChat now (pure Platform, M+M) + document voice workaround until the Stasis consumer lands.
