# Plan: Phase 3A — In-browser inbound voice (SIP.js/WebRTC softphone, audio MVP)

## Context

This is **Phase 3** of the *Inbound Conversation Delivery* epic (P1 WebChat ✅ shipped, P2 voice→queue ✅ shipped + proven with a real SIP call in the lab). P1/P2 made an inbound call **reach a queue and ring the agent's PJSIP endpoint** — but only on an **external SIP phone** (Zoiper/desk phone). The contact-center promise ("the agent answers in the browser with two-way audio") is still missing: the Web app has **zero** SIP/WebRTC code, voice is rendered as a text channel, and the agent's `sipPassword` is provisioned backend but the browser can't use it.

**Per the 2026-05-30 scoping decision, Phase 3 splits in two and we ship 3A first:**
- **3A (this plan)** — the *media/signaling* layer: a SIP.js softphone registers over WSS, an inbound call **rings in the browser**, the agent **answers**, gets **two-way audio**, and **hangs up**. Driven purely by the SIP session (no tracked Conversation yet). This is the risky "audio works" MVP.
- **3B (outline at bottom)** — the *business* layer: full call control (hold/transfer/mute/dialpad/outbound) + the AMI→Conversation bridge that turns the call into a tracked voice conversation (screen-pop, disposition, CDR, agent-assist). Its own plan at kickoff.

**User decisions baked into 3A:** (1) **3A then 3B**; (2) **WebRTC by default** for new agents (the existing `WebRTC Agent` endpoint profile becomes the resolved default; the `SIP Agent` UDP profile stays selectable and existing desk-phone agents are untouched); (3) **local lab, same host** — entrypoint auto-generates a self-signed cert, **no TURN**; Coturn + Let's Encrypt are documented production follow-ups, out of 3A.

**Outcome:** an operator provisions a browser agent end-to-end (extension + SIP password in the admin UI), the agent logs into `/agent`, the softphone REGISTERs to Asterisk, an inbound DID call rings the browser, the agent answers and talks. Once 3A ships and is lab-verified, manual 06 §"Fase 3" items flip from 🔜 to ✅ for the in-browser answer leg.

## Key finding (reshapes the plan)

**`sipPassword` is already over-exposed today.** [Agent.cs:16](../../../src/Verbara.Platform.Queues/Agent.cs#L16) declares `public string? SipPassword { get; set; }` with **no `[JsonIgnore]`**, and `Agent` is registered in `ApiJsonContext`. [AgentEndpoints.cs:26](../../../src/Verbara.Platform.Api/Endpoints/AgentEndpoints.cs#L26) (`/agents/me`) and the admin agent endpoints (`AdminEndpoints` `ListAgents`/`GetAgent`/`CreateAgent`/`UpdateAgent`) all return the **raw `Agent` entity**, so the secret is serialized on the wire — the Web TS interface just doesn't declare the field, which hid it. Workstream **C** therefore *tightens an existing leak into a deliberate, self-scoped exposure* (and adds regression tests locking the admin endpoints to never echo it).

## Process

Subagent-Driven with FCM batching, **TDD test-first per workstream**, 0-warning build, Native AOT (every new Platform DTO/event in `ApiJsonContext`; `[LoggerMessage]`), **Dapper banned** (Npgsql facade), Conventional Commits **no Co-Authored-By**, Web: `@base-ui/react` render-prop, TailwindCSS 4, **i18n parity EN-US/ES-419/PT-BR**, `data-*` E2E selectors. Each workstream commits green before the next. Recommended order: **C → A → B → D → E → F → H**.

Repos: Platform `/media/Data/Source/Verbara/Verbara.Platform` · Web `/media/Data/Source/Verbara/Verbara.Platform.Web` · Pro `/media/Data/Source/Verbara/Verbara.Sdk.Pro` (consumed via local feed).

---

## Workstream C — Self-scoped `sipPassword` on `/agents/me` (Platform + Web) — FIRST

Security fix + enabler for D. Stop serializing the `Agent` entity over HTTP; re-add the secret only where it belongs.

- New `AgentMeResponseDto` (sealed record): the existing `/agents/me` shape **plus** `Extension` + `SipPassword`, returned only by `GetCurrentAgent` (already self-scoped — resolves the agent from JWT `sub` via `GetByUserIdAsync`).
- New admin agent response DTO (e.g. `AdminAgentDto`) that **omits `SipPassword`** (may keep `Extension` to show provisioning state); used by `ListAgents`/`GetAgent`/`CreateAgent`/`UpdateAgent`. SipPassword is **write-only in, never out**.
- `[JsonIgnore]` on `Agent.SipPassword` (defense-in-depth so the entity can never serialize the secret again).
- Register `AgentMeResponseDto`, `AdminAgentDto`, `PagedResult<AdminAgentDto>` in `ApiJsonContext`; **grep `Results.Ok(agent)` / `PagedResult<Agent>` across all endpoints** before removing `[JsonSerializable(typeof(Agent))]` (SSE uses `AgentStateChangedEvent`, not `Agent`, so removal should be safe — verify).
- Web: add `sipPassword?: string | null` to `interface Agent` in `use-agents.ts` (only `/agents/me` populates it; admin hooks now never return it).

Files: `AgentEndpoints.cs`, new `AgentMeResponseDto.cs` (mirror `UsersMeResponseDto.cs`), `AdminEndpoints.cs`, `Agent.cs`, `ApiJsonContext.cs`.

Security note: plaintext-at-rest is **required** by the PJSIP realtime `ps_auths` (`auth_type=userpass`, Asterisk reads it plaintext to REGISTER the browser). Mitigation = TLS-in-transit (HTTPS `/agents/me`, WSS SIP) + self-scope + never in list/admin/logs. Token-broker / hashed-auth = future hardening, out of 3A.

TDD (Api.Tests): `GetCurrentAgent_ShouldReturnExtensionAndSipPassword_WhenRequestedByOwningAgent`; `GetCurrentAgent_ShouldNeverReturnAnotherAgentsSecret_WhenResolvedFromJwtSub`; `ListAgents_ShouldNotIncludeSipPassword_InResponseJson` (regression lock); `GetAgentById_ShouldNotIncludeSipPassword`. Web: extend `use-agents.test.tsx`.

## Workstream A — Self-signed cert auto-gen in the Asterisk entrypoint (infra)

WSS:8089 needs a cert; today `docker/asterisk-config/keys/asterisk.pem` + `.key` are **0 bytes**. Generate a self-signed pair on boot if missing/empty, idempotent, no-op when a real cert is mounted.

- **Permissions constraint:** the entrypoint runs as non-root `asterisk`, but `docker/asterisk-config/keys/` is root-owned + host-bind-mounted → an `openssl` write there `EACCES`s. **Solution (A1):** generate into an asterisk-writable runtime path `/var/lib/asterisk/keys/` and repoint `http.conf` `tlscertfile`/`tlsprivatekey` via the same env-injection `sed` idiom already in the entrypoint. This also keeps generated secrets off the host working tree.
- Prelude (before `exec asterisk -f`): resolve `CN=${EXTERNAL_IP:-$(hostname -i)}`; if cert/key missing or zero-byte → `openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -keyout … -out … -subj "/CN=$CN" -addext "subjectAltName=IP:$CN"` (DNS:localhost when not an IP); `chmod 600` the key. Cold-gen needs no reload.
- Ensure `openssl` exists in `Dockerfile.asterisk` (base usually ships it — verify). Reference format: `docker/demo/certs/asterisk.{pem,key}`.

Files: `docker/entrypoint-asterisk.sh`, `docker/asterisk-config/http.conf` (repoint paths), maybe `docker/Dockerfile.asterisk`, `.env.reference-smb.example` (document optional `ASTERISK_TLS_CERT/KEY` overrides).

Browser-trust caveat (document, don't solve): self-signed → the operator visits `https://{host}:8089/` once and accepts before WSS works; SIP.js surfaces failure only as a transport error.

TDD: `shellcheck` clean + a container smoke test (`EntrypointGeneratesCert_WhenKeysEmpty`, `…LeavesCertUntouched_WhenRealCertMounted`, `…Idempotent_OnSecondRun`) asserting `openssl x509 -noout -subject` + a successful `openssl s_client -connect host:8089`.

## Workstream B — WebRTC profile as the resolved default (cross-repo Pro bump)

Make the resolved default agent profile WebRTC **without** breaking SIP-UDP. `RealtimeSyncEngine.SyncAgentAsync(…, long? profileId = null, …)` resolves `profileId ?? GetDefaultAsync(Agent)`; `SeedDefaultsAsync` today seeds **SIP Agent = default**, **WebRTC Agent = non-default**.

Three coordinated changes in `Verbara.Sdk.Pro.Realtime.Storage.Postgres/PostgresEndpointProfileStore.cs`:
1. **Flip the seed** → `WebRTC Agent` `IsDefault=true`, `SIP Agent` `IsDefault=false` (fresh tenants default to WebRTC).
2. **Enforce single default per type** (the real correctness fix — `GetDefaultAsync` currently `LIMIT 1` with no deterministic order; two `is_default=true` rows resolve nondeterministically): in `UpdateAsync` (and seed) clear other `is_default` of the same `type` in a transaction.
3. **Idempotent migration for already-seeded tenants** — `EnsureWebRtcDefaultAsync(tenantId)` (or fold into the existing `POST /admin/realtime/profiles/seed-defaults` path which already calls `SeedDefaultsAsync`): promote `WebRTC Agent` → default + demote `SIP Agent` **only when the current default is exactly the seeded `SIP Agent`** (never override an operator's deliberate choice).

Impact: existing SIP-UDP agents are **untouched** at the running layer (`SyncAgentAsync` only rewrites on create/update); the default only affects newly synced agents without an explicit `profileId`. Mixed-fleet / per-agent profile selection = documented 3A limitation (operator re-seed or per-agent flip is a cheap follow-up). The admin profiles API already supports `PUT /admin/realtime/profiles/{id}` with `IsDefault` and the seed-defaults POST (`RealtimeEndpoints.cs`) — reuse them.

**Version bump:** `SeedDefaultsAsync` is compiled into `Verbara.Sdk.Pro.Realtime.Storage.Postgres` → bump Pro from `2.7.0-pro` to **2.7.1-pro** in Pro `Directory.Build.props`, `dotnet pack` to the local feed, clear cache, bump the matching pins in `Directory.Packages.props` (decide single-package vs lockstep band — verify no inter-Pro version pinning forces lockstep).

AOT: stays on the Npgsql facade, reflection-free, `[LoggerMessage]` for new logs.

TDD (Pro Realtime.Tests + IT): `SeedDefaultsAsync_ShouldMakeWebRtcProfileDefault_WhenTenantUnseeded`; `GetDefaultAsync_ShouldReturnWebRtcProfile_AfterSeed`; `UpdateAsync_ShouldDemoteOtherProfiles_WhenSettingNewDefault`; `EnsureWebRtcDefaultAsync_ShouldPromoteWebRtc_WhenStillOnSeededSipDefault`; `EnsureWebRtcDefaultAsync_ShouldNotOverride_WhenOperatorCustomized`; `ResolveAgentEndpointAsync_ShouldEmitWebrtcYes_WhenDefaultIsWebRtc`.

## Workstream D — SIP.js softphone subsystem (Web)

- **WSS host config:** extend `/config.json` with `asteriskWssUrl` (lowest friction — already fetched at boot in `use-config.ts`, per-deployment, no auth/AOT). reference-smb sets `wss://${EXTERNAL_IP}:8089/ws`. **Verify the actual WS path** (`/ws` is Asterisk's `res_http_websocket` default — NOT `/asterisk/ws`; pin during H).
- `src/core/voice/softphone-manager.ts` (new) — SIP.js `UserAgent`: `transportOptions.server = asteriskWssUrl`, `uri/authorizationUsername = {tenantId}-{extension}` (matches `ResolveAgentAuthAsync`), `authorizationPassword = agent.sipPassword`; a `Registerer` to REGISTER on start; `delegate.onInvite` → push to the voice-call-store (state `ringing`, callerId from `remoteIdentity`); media via SIP.js `SessionDescriptionHandler` (`constraints {audio:true,video:false}`), remote track → hidden `<audio autoplay>`, mic via `getUserMedia`. Start only when `agent.capacity.maxVoice > 0 && agent.extension && agent.sipPassword`.
- `src/agent/stores/voice-call-store.ts` (new, Zustand mirroring `conversation-store.ts`): states `idle|ringing|active|ended`; fields `callerId`, `remoteStream`, `startedAt`, a non-serialized handle to the SIP.js session; actions `incoming/answer/hangup/ended/reset`.
- Boot/teardown in `src/pages/agent/agent-layout.tsx` (the always-present shell — the conversation route unmounts; the layout doesn't), alongside the existing SSE init effect.
- `add sip.js` to package.json. sipPassword stays in-memory (TanStack `['agent-me']` cache) — never localStorage.

TDD (vitest, mock `sip.js`): registers with tenant-prefixed username+password; does-not-start without creds; onInvite → ringing+callerId; store transitions answer→active, hangup→ended, reset→idle.

## Workstream E — Minimal call-card UI (Web)

Store-driven only (no Conversation in 3A). `src/agent/voice/call-card.tsx` (new), mounted in `src/pages/agent/agent-layout.tsx`: renders nothing when `idle`; **ringing card** (callerId + Answer/Reject) when `ringing`; **in-call card** (callerId + live timer + Hangup) when `active`. Reuse `conversation-panel.tsx` toolbar/button patterns; hidden `<audio>` sink bound to `remoteStream`. `data-testid`s: `voice-call-card`, `voice-answer-btn`, `voice-reject-btn`, `voice-hangup-btn`, `voice-call-timer`, `voice-caller-id` + `data-voice-state`. i18n `voice.*` keys in **all three** locale `agent.json`.

TDD: renders-nothing-when-idle; Answer/Reject shown when ringing + callerId; Hangup+timer when active; buttons call store actions.

## Workstream F — Admin agent extension/sipPassword UI (Web)

So an operator provisions a browser agent end-to-end. `src/admin/agents/agent-form.tsx`: extend `agentSchema` with `extension`/`sipPassword` (optional) + two `Input`s + a "Generate" button (crypto-random 16-char). `src/admin/agents/agent-detail.tsx`: show `extension` read-only + a "reset SIP password" action (**never display the stored secret** — admin DTO won't return it post-C). Extend `useUpdateAgent` input in `use-agents.ts` to carry `extension?`/`sipPassword?` (backend `UpdateAgentRequest` already accepts them). i18n parity in `admin.json`.

TDD: form submits extension+sipPassword; generate button fills sipPassword; detail never renders the raw secret.

## Cross-cutting

- **AOT:** new Platform DTOs (`AgentMeResponseDto`, `AdminAgentDto`, `PagedResult<AdminAgentDto>`) **must** be `[JsonSerializable]` in `ApiJsonContext` or AOT serialization throws; `[LoggerMessage]` for new logs.
- **Cross-repo cycle (B):** Pro `dotnet pack -c Release -o /media/Data/Source/Verbara/local-nuget-feed/` → `cp` to repo-copy feed → `rm -rf ~/.nuget/packages/verbara.sdk.pro*` → `dotnet restore` Platform.
- **No company refs; Spanish convo / English code; commit local only — user pushes manually.**

## Risks (ranked)

1. **`getUserMedia` needs a secure context** — plain HTTP to a LAN IP blocks mic; only `localhost`/HTTPS works. Decide before H: access the agent app via `http://localhost` on the host, or terminate TLS at the nginx-gateway for the Web origin. (Separate from the WSS:8089 cert trust.) **Most likely thing to break the audio demo.**
2. **Self-signed cert trust** — WSS fails until the operator accepts `https://host:8089` once; add a softphone connection-error toast so it's diagnosable.
3. **WSS path** `/ws` vs `/asterisk/ws` — wrong path = silent REGISTER failure; one config value, pin in H.
4. **Lab media without TURN** — same-host + `icesupport=true` + host candidates should work; ensure `EXTERNAL_IP` = the IP the browser reaches (drives `ice_host_candidates` + WSS `local_net` ACL).
5. **WebRTC-default migration** — must be idempotent and must not override operator-customized defaults; the single-default invariant fix is mandatory (latent multi-`is_default` nondeterminism).
6. **Security regression scope** — grep every `Results.Ok(agent)` / `PagedResult<Agent>` before removing the entity's `[JsonSerializable]`.
7. **Pro bump blast radius** — all Pro packages pinned `2.7.0-pro` in lockstep; verify single-package bump restores cleanly.

## Verification (E2E, local lab — Workstream H)

1. Pack Pro 2.7.1-pro → feed; `docker compose -f docker/docker-compose.reference-smb.yml build asterisk`; set `.env.reference-smb` `EXTERNAL_IP` = host LAN IP; set served `config.json` `asteriskWssUrl=wss://${EXTERNAL_IP}:8089/ws`.
2. Up the stack (or host-run API like Phase 2). Confirm cert: `docker exec verbara-asterisk openssl x509 -in /var/lib/asterisk/keys/asterisk.pem -noout -subject` + `openssl s_client -connect ${EXTERNAL_IP}:8089` handshakes.
3. `POST /api/v1/admin/realtime/profiles/seed-defaults` → `GET …/profiles` shows WebRTC `isDefault:true`, SIP `false`.
4. Provision a browser agent in the admin UI (F): user + extension `1001` + generate sipPassword → triggers `SyncAgentAsync` (ps_endpoints webrtc=yes).
5. Accept the self-signed cert at `https://${EXTERNAL_IP}:8089/` once.
6. Log in as the agent at `/agent` (via `http://localhost` or HTTPS — secure context). Softphone REGISTERs: `pjsip show contacts` shows a `transport-wss` contact; grant mic.
7. Inbound SIP call (SIPp / throwaway docker like Phase 2) to a DID with a `did_route` → queue dials the WebRTC endpoint → browser **rings** (ringing card, callerId).
8. **Answer** → **two-way audio** (speak both directions / MoH audible + SIPp confirms mic); timer runs.
9. **Hangup** → card returns to idle, `pjsip show channels` clears.

Pass = register visible + inbound rings the browser + answer yields audible two-way audio + clean hangup. Cert-accept is a one-time per-browser manual gate (production = Let's Encrypt, out of scope).

Plus: Web vitest green (softphone-manager + voice-call-store + call-card + agent-form); Platform Api.Tests green (sip-exposure + leak regression); Pro Realtime.Tests + IT green (WebRTC-default + single-default); a Playwright spec asserting registration + a mocked-INVITE ringing card (real media is the manual H gate, not CI).

---

## 3B outline (so 3A leaves clean seams)

Adds **call control** + the **voice-as-tracked-conversation** bridge, activating the dormant `VerbaraCapacitySyncService.HandleVoiceCallStarted/EndedAsync` and the unused `Verbara.Sdk.Ami` client, leader-gated like `StasisInboundConsumer`/`VoiceLeaderResources`:
- **Softphone control:** hold/resume (re-INVITE), mute (disable local track), DTMF dialpad (`session.info()`), blind/attended transfer (`session.refer()`), outbound (`UserAgent.invite()`). Extend `voice-call-store` (`on_hold`,`muted`) + the call-card toolbar.
- **AMI→Conversation bridge:** leader-gated `AsteriskAmiListener` BackgroundService (`IAmiConnectionFactory.CreateAndConnectAsync` per `ClusterNodeOptions.Ami`), subscribe `ManagerEvent`s → Newstate/Dial/Bridge call `HandleVoiceCallStartedAsync`, Hangup call `HandleVoiceCallEndedAsync`; wire the no-op `HandleCapacityChangedAsync`.
- **Voice as Conversation:** correlate the SIP session ↔ a `Conversation` (channel `voice`) → screen-pop, disposition/wrap-up, CDR, agent-assist (transcript/sentiment, already SSE-wired). New `PlatformEvent` records `voice.call.started/.answered/.ended` in `ApiJsonContext`, delivered via `IEventDeliveryFilter` userId targeting.
- Clean seams 3A leaves: `voice-call-store` already models lifecycle (extend, don't rewrite); the store-driven call card swaps to conversation-driven without touching the softphone; `softphone-manager` holds the session (control methods attach there); `/config.json` carries the WSS host; capacity hooks + AMI client already exist, merely un-wired.

3B gets its own spec/plan at kickoff.
