# Epic: Inbound Conversation Delivery — WebChat + Voice reach agents end-to-end

## Context

Reconstructing the SMB channel manuales (04-webchat, 06-voz-sip) after the v2.6.0 release exposed that **the two V1 inbound channels do not deliver inbound conversations to a browser-based agent out-of-the-box** — a blocker for the SMB-first "first paying customer" goal (a contact center must route inbound to agents). Three layers are missing, verified in code (see `docs/research/2026-05-30-channel-inbound-delivery-gaps.md`):

1. **WebChat** conversations are created in `Queued` state with `Owner=null` and never assigned to a queue (`WebChatEndpoints` calls only `IInboundMessagePipeline.ProcessAsync`, never `router.RouteAsync`/`switchboard.AssignToQueueAsync` like `WebhookEndpoints.cs:104-108`). The auto-distribution worker skips owner-less conversations → invisible to agents. A separate event-type mismatch means the agent UI isn't pinged on offer. (Agent→visitor reply IS already wired.)
2. **Voice** inbound dead-ends: dialplan ships `Stasis(verbara,inbound,${EXTEN})` but **no process consumes ARI app `verbara`** → the call enters Stasis, nobody acts, `Hangup()` drops it. Inbound voice never reaches a queue.
3. **In-browser voice softphone does not exist** — no SIP/WebRTC library in the Web app; voice is treated as a text channel; on "Accept" the agent's browser has no audio. `sipPassword` is provisioned backend but the Web UI is blind to it. The AMI→conversation bridge (`HandleVoiceCallStarted/Ended`) is never called.

**Outcome:** a visitor's chat and an inbound phone call both reach an available agent who handles them entirely in the browser. Once each phase ships, its manual (04 / 06) is reconstructed against the now-working feature (the manuales become honest because the product works — closes the original task).

**Process:** Spec+Plan before code (project rule). Subagent-driven development with FCM batching. Each phase: TDD, 0-warning build, tests green, commit, before the next. `TreatWarningsAsErrors` + Native AOT + no reflection ([JsonSerializable] for all new DTOs/events).

---

## Phase 1 — WebChat inbound delivery (pure Platform + small Web fix)

Makes WebChat a complete agent channel. Independently shippable (no Asterisk lab needed).

**1.1 Wire routing in `WebChatEndpoints`** — in `HandleWebSocket` (`:142`) and `SendRestMessage` (`:183`), on the FIRST inbound message of a session, mirror `WebhookEndpoints.cs:104-108`: build `RoutingContext`, `await router.RouteAsync(ctx)`, `await switchboard.AssignToQueueAsync(convId, tid, routeResult.QueueId)`, then publish `ConversationStateChangedEvent` + `ConversationMessageEvent` (mirror `WebhookEndpoints.cs:90-96`). Track "already routed" per session via a flag on `WebChatSessionManager` so only the first message routes (avoids assigning empty sessions). Inject `IInboundRouter` + `IConversationSwitchboard` + `IConversationStore` + `IContactStore` (already DI-registered, Program.cs:167-172).
- File: `src/Verbara.Platform.Api/Endpoints/WebChatEndpoints.cs`, `src/Verbara.Platform.Channels.WebChat/WebChatSessionManager.cs`.

**1.2 Default-queue resolution** — new `DefaultQueueFallbackMiddleware` in the inbound chain AFTER `ChannelQueueMappingMiddleware`: Tier 1 read `TenantChannelConfig.Credentials["defaultQueueId"]` (makes the currently-inert channel-config field actually drive routing); Tier 2 fall back to the tenant's first active queue (`IQueueStore.ListAsync` filtered `IsActive`). Benefits all digital channels, not just webchat.
- Files: `src/Verbara.Platform.Routing.Inbound/Middlewares/DefaultQueueFallbackMiddleware.cs` (new) + register in `ServiceCollectionExtensions.cs` chain; reads `ITenantChannelConfigStore` + `IQueueStore`.

**1.3 Fix the offer→agent notification mismatch** — switchboard publishes `ConversationOfferedEvent` (`conversation.offered`) but the Web agent UI listens for `conversation.assigned` (`use-sse.ts:38`, `conversation-store.ts:129`). **Verify the canonical event name first** (don't break other listeners), then align: emit the event the agent UI expects (or subscribe the UI to `conversation.offered`). Surface the offered conversation live in the agent inbox.
- Files: `Web/src/core/hooks/use-sse.ts` + `Web/src/agent/stores/conversation-store.ts` (and/or the server event name).

**1.4 Tests (TDD):** `WebChatRoutingTests` (mirror `BotHandoffLogicTests` mocking `IConversationSwitchboard`; assert `RouteAsync` + `AssignToQueueAsync` called + events published, only on first message); `DefaultQueueFallbackMiddleware` test (Tier1 + Tier2). Templates: `tests/Verbara.Platform.Api.Tests/WebhookEndpointTests.cs`, `Routing.Inbound.Tests/InboundRouterTests.cs`.

**1.5 Verify E2E:** demo/reference-smb stack — visitor opens webchat → sends message → conversation appears in agent inbox as offered → agent accepts → replies → visitor receives. Add a Playwright round-trip spec (extend `Web/tests/e2e/.../webchat`).

**1.6 Manual:** reconstruct `04-canal-webchat.md` against the now-working flow (PUT not PATCH, `{isActive,credentials}` body, real `defaultQueueId`, Customer-tenant curls, remove fictional `/admin/routing/inbound` + analytics endpoint).

---

## Phase 2 — Voice inbound-to-queue (Platform + cross-repo Pro 2.7.0-pro)

Calls reach the queue and ring the agent's PJSIP endpoint. Needs the Asterisk lab to verify.

> **Implementation status (2026-05-31):**
> - **2.1 did_routes data layer — ✅ DONE** (commit `f451b04c`, not pushed). 8 files + 6 edits; 30 tests; migration 026 validated on live postgres:18 (`did_per_tenant_unique` fires); adversarial review caught + fixed 3 InMemory/Postgres parity defects.
> - **2.2 StasisInboundConsumer + leader-election — ✅ IMPLEMENTED** (unit-verified; lab E2E deferred to 2.6). New `StasisInboundConsumer` + `VoiceLeaderResources` + Program.cs leader wiring (keyed `"Cluster"` datasource = shared transport pool, `AddPostgresDistributedLock` + builder `AddVerbaraCluster(RegisterLeader("voice:stasis:inbound:leader"))` + post-build `MigrationRunner.EnsureSchemaAsync`, all gated on `clusterConn`) + csproj `Verbara.Sdk.Cluster.{Primitives,Postgres}` refs. 18 unit tests; Api 0 warnings; full Api.Tests **1075/1075**.
>   - **DESIGN DEVIATION (the line-90 risk, materialized):** the original plan resolved tenant from `evt.Channel.Name` (`PJSIP/t-{trunkId}-…` → trunk → tenant). **Verified impossible self-contained:** the inbound *trunk* channel name is NOT tenant-prefixed (trunk isolation lives in `ps_endpoints.tenantid`), the trunk store is **tenant-scoped** (can't reverse-resolve a tenant from a trunkId), and `IRealtimeStore` has **no** endpoint→tenant reverse lookup. **Shipped contract: tenant is read from the `TENANT_ID` channel variable** (`GetVariableAsync`), which the inbound endpoint sets via `ps_endpoints.set_var` — that `set_var` wiring is a **Phase 2.4 dependency** (RealtimeSyncEngine). The consumer **fails closed** (Hangup) whenever tenant / DID / route / queue is unresolved.
>   - Leader-gates the *connection* (only the leader opens the ARI WS) — stronger than the relay's per-event short-circuit, because a physical call can't be re-emitted. `StopHost`-safe: idle (no crash) when `Asterisk:Ari` unset; per-tick faults swallowed; rethrow only on fatal loop fault after a Critical `WorkerCrash` log.
> - **2.3 Dialplan — ✅ DONE.** `[stasis-queue]` added to `docker/asterisk-config/extensions.conf` as `exten => s,1,Queue(${QUEUE_NAME})`. The consumer sets the `QUEUE_NAME` channel variable to `{tenantId}-{queueName}` and continues to the **fixed extension `s`** — decoupling the dialplan from the queue-name charset so a hyphenated / mixed-case / spaced name can never miss a pattern and silently drop the call post-Answer.
> - **Adversarial 3-lens review (2026-05-31) — applied:** Lens-1 AOT/StopHost **clean**. Fixed **HIGH** (ConnectAsync orphaned a connected ARI client if `Subscribe` threw after connect → publish `_client` before subscribe so teardown always disposes it), **HIGH** (dialplan pattern could miss `{tenant}-{queueName}` for unvalidated tenant/queue slugs → the `s`+`QUEUE_NAME` redesign above), **LOW** (leader competed on no-ARI pods → leader+consumer now gated on `Asterisk:Ari:BaseUrl` so only ARI-capable pods win the lease), **MEDIUM** (silent return on empty channel id → Warning log), and a **resilience** gap (dropped WS left the leader connected-but-deaf → observer `OnError`/`OnCompleted` flag a fault, next tick tears down + reconnects). 4 new tests (22 total); full Api.Tests **1079/1079**.
> - **2.4 WS3 IP-ACL + TENANT_ID set_var — ✅ DONE** (cross-repo: Pro `72ea015` 2.6.0-pro→**2.7.0-pro** + Platform). Supplies the 2.2 contract: `RealtimeSyncEngine.SyncTrunkAsync` writes `ps_endpoints.set_var="TENANT_ID={tenantId}"` on the trunk endpoint (the inbound trunk channel id is `t-{trunkId}`, not tenant-prefixed). Plus IP-ACL: `Trunk.MatchHost` → `ps_endpoint_id_ips` identify row (id=`ipauth-{sipId}`, no digest auth for a fixed source IP/CIDR), idempotent (cleared MatchHost deletes it; tenant-wipe cleans trunk endpoints+identifies by the `tenantid` column). New `PjsipIdentifyRow` + `IRealtimeStore.UpsertIdentifyAsync/DeleteIdentifyAsync` + `PjsipEndpointRow.SetVar`. **Both schema objects pre-existed in `V001` (no migration).** Platform: `TrunkEndpoints` `MatchHost` in 3 DTOs + handlers (CIDR-validated, empty-string = clear) + 21 Pro pins bumped. **Adversarial review applied**: HIGH set_var injection (tenant id with `;`/`=`/newline → fail closed at producer); MEDIUM identify-orphan on tenant delete (clean by `tenantid` column); MEDIUM API couldn't clear MatchHost via null (empty-string sentinel → null) + misnamed test (now set + clear); LOW malformed-CIDR (400). Pro Realtime.Tests **190/190** + IT real-pg (set_var + identify + tenant-wipe); Platform Api.Tests **1086/1086**; 0 warnings cross-repo. **The contract is closed end-to-end: 2.4 writes TENANT_ID on the trunk endpoint → 2.2 reads it from the channel → tenant → did_route → queue.** Remaining: 2.5 (more tests, mostly done) / 2.6 lab E2E (SIPp inbound — needs Asterisk lab) / 2.7 manual 06.

**2.1 DID→queue mapping** — new `did_routes` table (migration `026_DidRoutes.sql`: tenant_id, did, queue_id, is_active, UNIQUE(tenant_id,did)) + `IDidRouteStore` (Postgres + InMemory, mirror `IQueueStore`) + `DidRouteEndpoints` (mirror `TrunkEndpoints`: `MapGroup("/admin/did-routes").RequireAuthorization("AdminOnly").RequireOperationalTenant()`, audited via `IAuditService`, typed sealed-record DTOs in `ApiJsonContext`).
- Migration template: `src/Verbara.Platform.Storage.Postgres/Migrations/025_QueueMembershipAllowedChannels.sql`.

**2.2 `StasisInboundConsumer : BackgroundService`** in Platform.Api — **leader-gated** via `IClusterLeader` (`RegisterLeader("voice:stasis:inbound:leader")` on the existing `AddVerbaraCluster`, Program.cs:818; pattern: `PushToHubRelay.cs:84/135`). Only the leader pod connects the ARI WS (stronger than Realtime's short-circuit, because a physical call can't be re-emitted). Own `IAriClient` via `IAriClientFactory.CreateAndConnectAsync` from `Asterisk:Ari` config. `Subscribe` → filter `StasisStartEvent` where `Args[0]=="inbound"`: resolve tenant from `evt.Channel.Name` (`PJSIP/t-{trunkId}-...` → trunk → tenant), look up `did_routes(tenant, Args[1]=DID)` → `queue_id` → `Queue.Name` → `AnswerAsync` → `ContinueAsync(channelId, "stasis-queue", "{tenantId}-{queueName}")`.
- File: `src/Verbara.Platform.Api/Services/StasisInboundConsumer.cs` (new). SDK surface: `Verbara.Sdk.Ari` (`AriClient`, `StasisStartEvent`, `AriChannelsResource.AnswerAsync/ContinueAsync`).

**2.3 Dialplan** — add `[stasis-queue]` context (`exten=_X.,1,Queue(${EXTEN})` / `same=>n,Hangup()`) to `docker/asterisk-config/extensions.conf`. The consumer passes the Asterisk queue name as the extension.

**2.4 WS3 — IP-ACL trunk identify (cross-repo Pro 2.6.0-pro → 2.7.0-pro):** `Trunk.MatchHost` (Pro.Dialer); `IRealtimeStore.UpsertIdentifyAsync`/`DeleteIdentifyAsync` + `PjsipIdentifyRow` (Pro.Realtime) writing `ps_endpoint_id_ips(id=ipauth-{sipId}, endpoint=t-{Id}, match=<IP/CIDR>)`; wire in `RealtimeSyncEngine.SyncTrunkAsync`/`RemoveTrunkAsync` when `MatchHost` set; `PostgresRealtimeStore` impl. Platform: `CreateTrunkRequest`/`UpdateTrunkRequest`/`TrunkDto` gain `MatchHost`, flows through `RealtimeSyncingTrunkStore`. Pack Pro → local feed, clear cache, bump pins in `Directory.Packages.props`.
- Pro files: `Verbara.Sdk.Pro.Realtime/{IRealtimeStore.cs,Engine/RealtimeSyncEngine.cs}`, `…Realtime.Storage.Postgres/PostgresRealtimeStore.cs` + migration, `Verbara.Sdk.Pro.Dialer/Models/Trunk.cs`.

**2.5 Tests:** `IDidRouteStore` + `DidRouteEndpoints` (mirror trunk tests); `StasisInboundConsumer` unit (mock `IAriClient` + `IClusterLeader` + `IDidRouteStore`: assert Answer+Continue when leader, no-op when follower, tenant parse from channel name); Pro `UpsertIdentify` + `SyncTrunkAsync` identify tests.

**2.6 Verify E2E (lab):** SIPp inbound call to a DID with a `did_route` → call lands in `{tenant}-{queue}` → rings agent PJSIP endpoint. Leader-gate: scale api to 2 replicas, confirm single handler. IP-ACL trunk INVITE from a fixed IP matches after `MatchHost` set.

**2.7 Manual:** reconstruct `06-canal-voz-sip.md` + checklist §7 against real endpoints (`/admin/trunks`, `did_routes` CRUD, no `provision-webrtc`/`/dialer/*`).

---

## Phase 3 — Voice in-browser softphone (Web + Platform AMI bridge)

The agent answers calls in the browser. The largest phase; may warrant its own detailed sub-plan at execution kickoff.

**3.1 sipPassword exposure (Platform + Web):** `/agents/me` returns the owning agent's `Extension` + `SipPassword` (security: only to the agent themself). Web `Agent` DTO (`use-agents.ts`) gains `sipPassword`. Admin `agent-form.tsx` + `agent-detail.tsx` capture/display `extension` + `sipPassword` (auto-generate option); setup wizard agent step optionally captures extension.

**3.2 SIP.js softphone subsystem (Web):** add `sip.js`; `core/voice/softphone-manager.ts` (a `UserAgent` registering to `wss://{asterisk}:8089/asterisk/ws` with extension+sipPassword on agent login); `core/voice/use-sip-invitations.ts` (inbound INVITE → emit to store); `agent/stores/voice-call-store.ts` (call state); WebRTC media (`<audio>` sink, `getUserMedia`, `RTCPeerConnection` via sip.js).

**3.3 Call-control UI:** enhance `agent/conversation/conversation-panel.tsx` — answer/reject/hold/resume/mute/transfer/hangup tied to SIP signaling; dialpad; CLID display; call timer; device selection.

**3.4 AMI→conversation bridge (Platform):** `AsteriskAmiListener` service subscribing to Asterisk AMI — on agent-extension call events, create/update the voice `Conversation` and emit SSE so the agent UI's call card stays in sync with the SIP session; wire the dormant `HandleVoiceCallStartedAsync`/`HandleVoiceCallEndedAsync` (`VerbaraCapacitySyncService`).

**3.5 Tests:** Web vitest for `softphone-manager` + `voice-call-store` (mock sip.js); Playwright for call-control UI; Platform `AsteriskAmiListener` tests.

**3.6 Verify E2E (lab):** inbound call (Phase 2) → agent browser softphone registered → rings → agent answers in browser → two-way audio (lab SIP test endpoint) → hold/transfer/hangup work.

---

## Cross-cutting

- **AOT:** all new Platform DTOs/events in `ApiJsonContext` / source-gen contexts; no reflection; `IsAotCompatible` preserved.
- **Multi-tenant:** `did_routes` tenant-scoped; Stasis tenant resolved per-call from the channel/endpoint.
- **Pro version:** 2.6.0-pro → **2.7.0-pro** (Phase 2 WS3 only).
- **Docs:** on approval, mirror this plan to `docs/plans/active/2026-05-30-inbound-conversation-delivery.md` + write the design spec under `docs/specs/`; move to `completed/` on ship. Reconstruct manuales 04/06 per phase (1.6 / 2.7).
- **Sequencing:** P1 (independent, ship first) → P2 (Asterisk lab) → P3 (depends on P2). Each phase commits green before the next.

## Risks / open items

- **Offer-event mismatch (1.3):** confirm the canonical event name and all listeners before changing — avoid breaking other channels' notifications.
- **Tenant resolution from `Channel.Name` (2.2):** fragile if the PJSIP name format varies; consider a dialplan channel-var (`tenantid`) fallback as a follow-up.
- **WebRTC media in lab (P3):** needs working WSS/cert + STUN/TURN; reference-smb ships defaults — verify early in P3.
- **P3 size:** 1-3 weeks; if it balloons, split into its own spec/plan at kickoff (softphone-registration → call-control → AMI-bridge sub-phases).

## Verification summary

- P1: Playwright webchat round-trip on demo stack + unit tests green.
- P2: SIPp inbound → queue → PJSIP ring; 2-pod leader-gate test; IP-ACL match.
- P3: lab call → browser softphone answer → audio + full call control.
