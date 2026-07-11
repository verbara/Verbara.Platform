# Roadmap — Verbara.Platform + Verbara.Platform.Web

**Última actualización:** 2026-07-11 · **Baselines actuales (released, tagged + GH Release + cosign):** Platform **`v2.16.0`** · Platform.Web **`v3.11.0-web`** · SDK **`2.2.1`** · Pro **`2.8.0-pro`** (on GitHub Packages). **⏳ Platform `v2.18.0` + Web `v3.13.0-web` "CSAT consumer (digital-first)" — in the CSAT Runner train (Pro `2.9.0-pro` pin advanced; ADR-0020); voice/TTS deferred to a Pro Path-A follow-up.** **✅ 2026-06-28 AI-credit ledger `v2.16.0`** (signed append-only ledger + O(1) balance projection; inert behind 2 default-off kill-switches; ADR-0033) · **✅ 2026-06-24 Typification P2c.2 `v2.15.0` + Web `v3.11.0-web`** (platform-managed metered LLM / AI Credits) · **✅ 2026-06-23 `v2.14.1`** (impersonation + rate-limiter fail-closed fixes) — the three above were **backfilled into the Shipped table 2026-07-01** (had drifted since v2.14.0). **🟢 2026-07-01 `typification-autonomous-disposition` (E5, reframed — ADR-0034) merged to `main` — dark + unreleased** (see "On main, sin release"). **✅ 2026-06-22 Typification P2c.1 (per-tenant BYO LLM config, BYO-only) SHIPPED end-to-end** — Platform **`v2.14.0`** + Platform.Web **`v3.10.0-web`** (PR #74+#117 via merge queue; 4 AOT/IL + Web cosign images; GH Releases; api digest `6cc1a9ef…` registered in authorized-digests #34, Worker auto-deployed). Multi-provider pluggable (OpenAI-compat/Azure/Anthropic), encrypted creds, fail-closed, AI strictly opt-in. **BREAKING (multi-tenant):** the shared global LLM key is retired (single-tenant/dev auto-seeded). Also folds the **#72 auth-drain fix**. _Platform-managed LLM as a metered/gated/billed service = **P2c.2** (next)._ **✅ 2026-06-21 Typification P2b SHIPPED end-to-end** — Platform **`v2.13.0`** (API: PR #70+#71, 4 imágenes AOT/IL cosign-signed, GH Release, api digest en authorized-digests) + Platform.Web **`v3.9.0-web`** (UI: PR #112+#116). `release.yml` runs verdes (Platform 27905988770 / Web 27906388147). _Tren Session/Auth (2.9.x) + Typification (P0–P2b, 2.10.0→2.13.0) añadido a la tabla Shipped el 2026-06-18 (estaba drifted desde 2026-06-01)._ **✅ 2026-06-01 v2.7.0 (Inbound Conversation Delivery) + v2.8.0 (Telephony admin: trunk+DID) + v2.8.1 (realtime reference-smb hotfix) SHIPPED** — releases un-deferred; see Shipped table + [[project-telephony-admin-v281]]. _Histórico previo:_ Platform **`2.5.4 + ADR-0026 Phase A + Phase B en main`** (post-Phase A.5 + ADR-0025 K8s health contract fix + JWT Tier-1 hardening + observability + Phase A wizard fix + Phase B membership executive gate) · Platform.Web **`3.1.3-web` + Phase A editor en main** · SDK pin **`2.2.1`** · Pro pin **`2.6.0-pro`** (bumped 2026-05-29 para Phase B `IRealtimeSyncService.AddQueueMemberAsync(allowedChannels)` signature change) · **🎉 visibility flip EXECUTED 2026-05-10 — Platform + Web repos PUBLIC** · **✅ 2026-05-22 ADR-0022 Phase D CLOSED** (Native AOT shipping) · **✅ 2026-05-24 Phase A.5 CLOSED** (Plan B Test 5 PARTIAL→PASS) · **✅ 2026-05-25 ADR-0025 K8s health contract + Phase B-LK + Phase C-LK closed on v2.5.2** · **✅ 2026-05-25 JWT Tier-1 hardening + lab causality on v2.5.4** (TTL bump = primary driver; stale-cache fallback = insurance, never fired in lab) · **✅ 2026-05-26 R5.5 Phase D-LK 24h soak PASS + R5.5 train SHIPPED with Production Readiness Review** · **✅ 2026-05-28 ADR-0026 Phase A + ADR-0027 tenant-type gate SHIPPED** · **✅ 2026-05-29 ADR-0026 Phase B SHIPPED** (digital-routing executive gate; SMB Docker product polish = ZERO routing tech debt; release packaging deferred to first paying customer).

> ### 🛑 STRATEGIC PIVOT 2026-05-25 — No cloud until real customers exist
>
> Maintainer directive: *"Todo el trabajo que tengamos que realizar en la nube queda pospuesto por presupuesto hasta que no existan clientes reales (...) lo primordial es ya tener un producto final probado y funcional el cual inicialmente estará enfocado en docker; k8s es para clientes mas grandes que sera captados a través de los resultados de los clientes pequeños."*
>
> **Effects:**
> - **Phase 0C / 0CK / 0CR / R5.6 cloud-K8s sprint** → deferred indefinitely. R5.5 closes with docker + K8s-local datasets only (no cloud comparison dataset).
> - **JWT Tier-2 (SCAN→MGET) + Tier-3 (`IssuerSigningKeyResolverAsync`)** → blocked, data-gated on production cloud telemetry that won't exist pre-revenue. Tier-2 spec stays as ready-to-execute reference (`docs/specs/2026-05-25-jwt-tier-2-redis-set-index.md`).
> - **NetworkChaos #06+#07 + C-LK.3 etcd/apiserver chaos** → documented as known limit (Cilium eBPF + single-CP lab). Defer until customer footprint requires it.
> - **Customer-acquisition flywheel:** SMB Docker reference deployment polish → first paying customers → demonstrated results → larger K8s/cloud customers. That's the order.
>
> **Primary track now:** SMB Docker product polish (Fase 1 SMB ya shipped 2026-05-17 — manuales 12 docs + `docker-compose.reference-smb.yml` + `quickstart-smb.sh` + 7 E2E tests). Re-audit + sync vs Platform v2.5.4 behavior. K8s local is secondary track.
>
> Re-evaluation trigger: first paying customer onboarded OR explicit maintainer override.

> **Authoritative source** — por decisión 2026-04-19, este repo es el workstream autoritativo para todo lo que cruza API + Web. Plans, specs, ADRs y research viven aquí. `Verbara.Platform.Web` sigue siendo repo separado para código frontend, pero su planning se origina en este árbol `docs/`.

Para el roadmap **downstream** (SDK y SDK.Pro) que alimenta este stack: `Verbara.Sdk.Pro/docs/roadmap.md`.

---

## Shipped (histórico condensado)

| Versión | Fecha | Tema | Deps |
|---|---|---|---|
| Platform 1.0.x | 2026-03 | Foundation, auth, RBAC, 11 channels | SDK ≤1.4 / Pro ≤1.0-pro |
| Platform 1.1.0 "Enterprise Ready" | 2026-03-26 | JWT+MFA+OIDC, 64 perms, 8 role templates | — |
| Platform 1.2.0 "Monetization Ready" | 2026-03-31 | Billing (metering+quotas+rate cards+invoices), Platform Admin, E2E Playwright | — |
| Platform 1.2.1 "Operations" | 2026-03-31 | DTO hardening, Server Mgmt API, Impersonation, Cluster UI | Pro 1.1.1-pro |
| Platform 1.3.0 "Integration & Compliance" | 2026-04-03 | License enforcement, OIDC SSO, GDPR, outbound webhooks | — |
| Platform 1.3.1 "Operational Maturity" | 2026-04-04 | API versioning /api/v1/, rate limiting, audit (phase 1), license gates, scheduled reports, webhook circuit breaker | — |
| Platform 1.4.0 | 2026-04-08 | Plan 31 core operations (canned responses, cases, dispositions, surveys) | — |
| Platform 1.5.0 "Production Ready" | 2026-04-09 | Plan 32 critical fixes, Web sync | — |
| Platform 1.6.0 "Production Polish" | 2026-04-11 | Subs A/C/D/E (Sub B deferred) | — |
| Platform 1.7.0 "Reseller Enablement" | 2026-04-13 | Partner portal, white-label, onboarding, impersonation | SDK 1.8.0 + Pro 1.2.0-pro |
| **Platform 1.8.0** | **2026-04-19** | **Plan 32C "Real-Time Presence" end-to-end** — PlatformHub wired, SignalR supervisor actions, frontend @microsoft/signalr 10, realtime store + hooks + E2E | **SDK 1.11.1 + Pro 1.7.2-pro** |
| **Platform 1.8.1** | **2026-04-19** | **Pro 1.8.x "Enterprise Ready" consumer bump** — `AddProResilience` + `AddProLicenseGuard` + `AddProRetention` (DryRun default) + 5 retention targets wired. 0 breaking, 0 UI change, 1,644 unit tests green. | **SDK 1.11.1 + Pro 1.8.1-pro** |
| Platform 1.9.0 "Secure + Current" (R3) | 2026-04-20 | Security + dependency-current foundation; opens R3 release train | SDK 1.13.x + Pro 1.9.0-pro |
| Platform 1.9.1 "Resilience Coverage" (R3b) | 2026-04-21 | Resilience policy expansion | SDK 1.13.x + Pro 1.9.x-pro |
| Platform 1.9.2 "Hardening Follow-Through" (R3c) | 2026-04-21 | JWT hardening (`jti`, DataProtection wrap, fingerprint kid, kill `?token=`, `IJtiRevocationCache`) | SDK 1.13.x + Pro 1.9.x-pro |
| Platform 1.9.3 | 2026-04-21 | Speech Analytics dashboard + Compliance Aggregations API | SDK 1.13.x + Pro 1.9.3-pro |
| Platform 1.10.0 "Production Readiness + Ops Toolkit" (R5.1) | 2026-04-22 | First R5 release; live queue metrics pipeline + AgentAssist runtime feature toggle | SDK 1.15.0 + Pro 1.12.0-pro |
| Platform 1.11.0 "Security Admin + Compliance Path" (R5.2) | 2026-04-26 | Canonical multi-tenant tenant-stamping pipeline (ADR-0002); audit endpoints; webhook DLQ admin | SDK 1.15.0 + Pro 1.13.0-pro |
| Platform 1.12.0 "Admin Completeness + R4 Closure" (R5.3) | 2026-04-26 | Strict-mode SignalR + cross-tenant validation (ADR-0005); R4 Track A declared COMPLETE | SDK 1.15.0 + Pro 1.14.0-pro |
| Platform 1.13.0 "Production Validation" (R5.4) | 2026-04-26 | Internal security audit baseline (ADR-0008) + SLO baseline + alert severity model (ADR-0009); **R5 Production Readiness Release Train DECLARED COMPLETE** | SDK 1.15.1 + Pro 1.15.0-pro |
| Platform 1.14.0 "Auth Hotpath Hardening" (AHH train opens) | 2026-04-27 | Auth perf + concurrency hardening; opens v1.14.x patch train | SDK 1.15.1 + Pro 1.15.0-pro |
| Platform 1.14.1 | 2026-04-28 | AHH empirical follow-up + multi-replica scaffold | SDK 1.15.1 + Pro 1.15.0-pro |
| Platform 1.14.2 | 2026-04-28 | AHH multi-replica unblocked + Argon2id retune + Postgres pool sizing | SDK 1.15.1 + Pro 1.15.0-pro |
| Platform 1.14.3 | 2026-04-28 | PLATFORMAPI patches: 500→409 on duplicate + `?email=` filter | SDK 1.15.1 + Pro 1.15.0-pro |
| Platform 1.14.4 | 2026-04-28 | Known-debt patches — closes [AUTH-002](security/internal-audit-2026-04.md), CFG-003 partial, MFA-007 partial | SDK 1.15.1 + Pro 1.15.0-pro |
| Platform 1.14.5 | 2026-04-28 | [ADR-0015](decisions/0015-npgsql-datasource-sharing-strategy.md) Phase 1 — Postgres connection-pool sprawl mitigation | SDK 1.15.1 + Pro 1.15.0-pro |
| Platform 1.14.6 | 2026-04-28 | [ADR-0015](decisions/0015-npgsql-datasource-sharing-strategy.md) Phase 2 — shared `NpgsqlDataSource` adoption across composition root | SDK 1.15.1 + Pro 1.16.0-pro |
| Platform 1.15.0 "Pre-v2 Foundation" | 2026-05-02 | Final pre-rebrand baseline; alignment with SDK 2.x preparation | SDK 1.15.x + Pro 1.16.0-pro |
| **Platform 2.0.0 "Verbara"** | **2026-05-05** | **Verbara rebrand** — full namespace + package rename per [ADR-0016 license + rebrand](decisions/0016-license-and-rebrand-to-verbara.md) and [ADR-0017 rebrand execution](decisions/0017-verbara-rebrand-execution.md); **Apache 2.0** license adopted; pre-rebrand artefacts archived under `pre-rebrand` tag | **SDK 2.1.0 + Pro 2.0.0-pro** |
| **Platform 2.0.1 "Trigger 3 closure"** | **2026-05-10** | **Security**: closes 2 P0 + 4 P1 findings raised in the 2026-05-09 pre-public security review (`docs/security/2026-05-09-pre-public-security-review.md`); new `TenantBoundaryValidationMiddleware`, OIDC client-secret encryption, scope-aware management API keys ([ADR-0019](decisions/0019-scope-aware-management-api-keys.md)); 35 new regression tests; threat model published; unblocks [ADR-0018](decisions/0018-visibility-decision-3-private-now-public-on-trigger.md) Trigger 3 (visibility-flip 6/7 GREEN) | **SDK 2.1.2 + Pro 2.2.0-pro** |
| **Platform 2.1.0 "Image-binding + visibility flip"** | **2026-05-10** | **Trigger 5 closure + repo PUBLIC**. (a) Pro v2.3.0-pro cascade — consumer adopts image-binding API surface (`LicenseGenerator.Generate(..., imageDigest)`, runtime `IMAGE_DIGEST` env-var check on startup per [Pro ADR-0011](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/decisions/0011-image-digest-binding-in-license-keys.md)); (b) GitHub Actions `release.yml` builds + pushes to `ghcr.io/verbara/platform/api`, signs with cosign v2.5.2 (Sigstore keyless OIDC), publishes manifest digest to job summary; (c) Helm chart `infra/k8s/helm/platform` defaults `image.repository=ghcr.io/verbara/platform/{api,web}` + injects `IMAGE_DIGEST` env-var on Deployment when `api.image.digest` set; Kyverno admission policy template enforces cosign verification cluster-wide; (d) docker-compose verification toolkit (`docker-compose.verified.yml` + `verbara-verify-image.sh` cosign pre-flight script) ships paridad single-host; (e) [ADR-0019](decisions/0019-scope-aware-management-api-keys.md) deprecation warning emitted on wildcard `platform:*` API key use (back-compat through v3.0.0); (f) **first signed image published** `ghcr.io/verbara/platform/api@sha256:f82a9041dc7f26018f6b6b11addf3ddbda6a7833827434f6b8d5ca2486349902` registered in `verbara-website/data/authorized-digests.json` (commit `2e41314`); (g) **🎉 visibility flip executed 19:04 UTC** — `gh api -X PATCH repos/verbara/Verbara.Platform -f visibility=public` succeeded (coordinated with Web); secret scanning + push protection enabled (free tier post-flip); Apache 2.0 declared in repo metadata; ADR-0006 economics now operating | **SDK 2.1.2 + Pro 2.3.0-pro** |
| **Platform 2.2.0 "Pro v2.4.0-pro consumer migration"** | **2026-05-18** | Pro v2.4.0-pro cascade + HTTP 402 RFC 9457 + license-status admin surface (`GET /management/system/license/status` `PlatformAdminOnly`). SMB operator docs migrated `LICENSING_MODE` → `LICENSE_PATH`. 32 files / 2,048 tests passing. ADR-0012 cycle: transition release consumed. | **SDK 2.1.2 + Pro 2.4.0-pro** |
| **Platform 2.3.0 "Worker Resilience"** | **2026-05-18** | Cross-repo train with **Pro v2.4.1-pro**: 27 BackgroundService workers hardened (Pro side); Platform wires `HostOptions.BackgroundServiceExceptionBehavior = StopHost` so silent-death failures crash the pod cleanly. 48 net new resilience tests (Platform 961; cumulative cross-pkg 3,112). 0 warnings. Driven by 2026-05-18 R5.5 K8s Phase D-LK forensics finding (`project_dlk_bundled_with_v250pro.md`). ADRs Platform-0021 + Pro-0013. | **SDK 2.1.2 + Pro 2.4.1-pro** |
| **Platform 2.4.0 → 2.4.1 "ADR-0022 Phase D — Native AOT cutover"** | **2026-05-20 → 2026-05-22** | **First Native AOT shipping release.** Phase 5 cutover: SDK 2.2.0 (new `Verbara.Sdk.Data.Npgsql` facade) + Pro 2.5.0-pro (full Dapper removal, `BanDapperPackageReferences` MSBuild guard) consumed. Platform v2.4.0 ships AOT-compatible source; v2.4.1 = 4 cosign-signed images on `ghcr.io/verbara/platform/{api,web,renderer,mail}` per [ADR-0023](decisions/0023-publishing-non-aot-microservices.md), digests authorized in license trust. Phase 6 — **24h AOT soak PASSED** against `ghcr.io/verbara/platform/api:v2.4.1` (803M req / 0 fail / p99 25 ms / no leak / pg_conns 11 flat / 0 restart). Hard constraint enforced: every shippable image is now Native AOT so closed-source Pro IP never ships as decompilable IL. | **SDK 2.2.0 + Pro 2.5.0-pro** |
| **Platform 2.4.2 "Lab migration prep + Pro v2.5.1-pro pin"** | **2026-05-23** | Bump to Pro **v2.5.1-pro** (ADR-0022 Phase A.5 per-resource leader election scaffold). Active plans modified same day: [`2026-05-22-phase-a5-cluster-leader-election.md`](plans/completed/2026-05-22-phase-a5-cluster-leader-election.md) (consume `LeaderElectionService` in Realtime/Cluster hot paths), [`2026-05-23-phase-a5-talos-smoke-test.md`](plans/completed/2026-05-23-phase-a5-talos-smoke-test.md), [`2026-05-23-lab-migration-v2.3.1-to-v2.4.3.md`](plans/completed/2026-05-23-lab-migration-v2.3.1-to-v2.4.3.md). Initial wiring partial on local branch — `Verbara.Platform.Realtime/Services/PushToHubRelay.cs` + `RealtimeLeaderResources.cs` + `Realtime.Tests/Services/PushToHubRelayTests.cs` import `Verbara.Sdk.Pro.Cluster.Leadership`. **Cross-repo validation 2026-05-23**: clean restore + 0 warnings build / 0 errors + 1,728/1,728 tests passing across 20 DLLs (Api.Tests 936/936 · Realtime.Tests 26/26) + Native AOT publish clean (67MB ELF, 0 IL2*/IL3* warnings). Pro v2.5.1-pro consumed end-to-end; no patch v2.5.2-pro required. | **SDK 2.2.1 + Pro 2.5.1-pro** |
| **Platform 2.5.0 → 2.5.4 "K8s health contract + JWT Tier-1 hardening + observability"** | **2026-05-24 → 2026-05-25** | Same-week train: **v2.5.0** Pro v2.5.0-pro consumer (RemoteEventDispatcher); **v2.5.1** PascalCase fix closing Plan B Test 5 SignalR exactly-once PARTIAL→PASS; **v2.5.2** ADR-0025 K8s liveness/readiness contract fix (`/health` becomes `Predicate=_=>false` no-op + chart defensive `timeoutSeconds:1→3` + `failureThreshold:3→5`) — B-LK rerun: 0 pod restarts (vs prior 4), 0 Unauthorized (vs prior 1000), p99 3.7s (vs prior 12.4s); **v2.5.3** JWT Tier-1 hardening (TTL 60s→5min + stale-cache fallback + fail-closed throw); **v2.5.4** OTel `verbara.platform.jwt` meter exposed for causality measurement. Validated: TTL bump is primary driver; Tier-1 fallback never fired in lab (insurance for production-cloud adversarial Redis). | **SDK 2.2.1 + Pro 2.5.1-pro** |
| **ADR-0026 Phase A + Phase B "Membership executive routing"** | **2026-05-28 → 2026-05-29 (`main` only, no tag)** | Two-day train. **Phase A** (2026-05-28): wizard `createdQueueId` flowthrough + agent-step "create new user" + channel-aware `queue_memberships.allowed_channels TEXT[]` + REST + Web editor at `/admin/agents/{id}/queues` + Day 2 living-docs journey + 13 Api.Tests. **Phase B** (2026-05-29, calendar gate retired): SDK Pro v2.6.0-pro [`913ec98`](https://github.com/verbara/Verbara.Sdk.Pro/commit/913ec98) ships `IRealtimeSyncService.AddQueueMemberAsync(allowedChannels)` with voice-gate encapsulated in `RealtimeSyncEngine` + Platform [`b731c1fc`](https://github.com/verbara/Verbara.Platform/commit/b731c1fc) + [`a6220698`](https://github.com/verbara/Verbara.Platform/commit/a6220698) consumes it: `IRoutingEligibilityService` + `MembershipAwareRoutingEligibilityService` + `RoundRobinAgentSelector` penalty-grouped + sticky bypass + `RealtimeReconciliationService` forward-only convergent BackgroundService (60s, meter `Verbara.Platform.Realtime.Reconciliation`) + `scripts/infer-memberships-from-skills.sh` idempotent legacy backfill + 13 new tests (Api.Tests 1013→1017, Routing.Inbound.Tests 32→41) + SMB manuals 03/04 refreshed (closes ADR-0027 C.2 deferred). **Design deviation**: forward-only reconciler instead of `IRealtimeVerifier.VerifyAllAsync` diff (latter needs `IAmiConnection` per tenant). **Net result**: `queue_memberships` is the executive source for ALL channels; **SMB Docker product polish = ZERO routing tech debt**. Release packaging deferred to first paying customer per 2026-05-25 pivot. | **SDK 2.2.1 + Pro 2.6.0-pro** |
| **Platform v2.7.0 + Web v3.3.0-web "Inbound Conversation Delivery"** | **2026-06-01 (tagged + GH Release)** | Closed the inbound-delivery epic: WebChat→queue (P1) + voice→queue Stasis consumer + did_routes + IP-ACL trunk (P2) + in-browser SIP.js/WebRTC softphone (3A) + voice-as-tracked-Conversation + screen-pop + agent-assist (3B.0/3B.1) + in-call control/auto-answer/blind-transfer/outbound (3B.2). 4 cosign-signed images + Web. **3B.3 (supervisor monitor + attended transfer + conference) = future.** | **SDK 2.2.1 + Pro 2.7.3-pro** |
| **Platform v2.8.0 + Web v3.4.0-web "Telephony admin (trunk + DID)"** | **2026-06-01 (tagged + GH Release)** | Made SIP telephony configurable from the UI (was `curl`-only). Audit [`docs/research/2026-06-01-trunk-did-audit.md`] → P0/P1/P2: persist trunk `match_host` (Pro **2.7.4-pro** V002, fixes IP-ACL drop-on-edit), complete trunk-form (basic/advanced), DID module `/admin/did-routes` + E.164/queue validation, guided trunk **wizard** (provider templates + reusable `wizard-layout`), leader-gated connectivity-test endpoint + UI. Api.Tests 1142. **Release ordering gate:** Pro must hit GH Packages before the Platform tag (v2.8.0 release.yml failed once, re-ran after Pro publish). P3 deferred (TLS/SRTP, resolver regex/digitmask + overflow, DID→IVR/Flow/Agent). | **SDK 2.2.1 + Pro 2.7.4-pro** |
| **Platform v2.8.1 "reference-smb realtime hotfix"** | **2026-06-01 (tagged + GH Release)** | Patch over v2.8.0, **no API code change**. v2.8.0 `Verbara.Platform.Realtime` hard-requires `ConnectionStrings:Cluster`/`:Postgres` for leader election; reference-smb compose provided neither → realtime crash-loop on fresh single-host. Added a Postgres leader-election conn to the realtime service. (K8s unaffected — already had `ConnectionStrings__Cluster`.) | **SDK 2.2.1 + Pro 2.7.4-pro** |
| **Platform v2.9.0 "Session/Auth overhaul"** | **2026-06-07 (tagged)** | Agent presence, liveness & work continuity — **ADR-0009 W1–W6**: idle + absolute session timeout, agent liveness, deferred-pause, work-failover, voice callback rescue, agent capacity. | SDK 2.2.1 + Pro 2.7.4-pro |
| Platform v2.9.1 | 2026-06-07 (tagged) | Audit category vocabulary fix. | SDK 2.2.1 + Pro 2.7.4-pro |
| **Platform v2.10.0 "Typification P0"** | **2026-06-07 (tagged)** | First-class schema-driven **cascading + conditional disposition forms** (clean-break of the flat Disposition). Opens the Typification train (**ADR-0029**). | **SDK 2.2.1 + Pro 2.7.5-pro** |
| **Platform v2.11.0 "Typification P1"** | **2026-06-08 (tagged)** | Shared taxonomy capture across the disposition module. | SDK 2.2.1 + Pro 2.7.5-pro |
| **Platform v2.12.0 "Typification P2a"** | **2026-06-10 (tagged)** | **AI auto-disposition** — classifier suggests node path + field values + confidence at wrap-up (first real LLM integration); deterministic binding/hint resolution. New `Verbara.Platform.Llm` seam + `OpenAiCompatibleLlmProvider`. | **SDK 2.2.1 + Pro 2.8.0-pro** |
| **Platform v2.13.0 + Web v3.9.0-web "Typification P2b"** | **2026-06-21 (tagged + GH Release + cosign)** | Human-in-the-loop AI **AutoFill** of the wrap-up form: graduated calibration-gated bands (Off/Shadow/SuggestOnly/AutoFill), server-authoritative provenance + `ai`-actor audit, entity prefill under a PII allow-list, per-binding override, per-tenant token budget (fail-closed) + `llm` rate-limit, prompt-injection + AOT fail-safe hardening. Also folded into the image (not in the `[2.13.0]` CHANGELOG body): **MessagePack 2.5.301 (CVE-2026-48109)** + **AOT cross-pod event-serialization guard** — both narrated in the GH Release. Web `v3.9.0-web` = admin Mode selector + bands + calibration panel + entity-map/PII editor + anti-clobber AutoFill UX (PR #112+#116). **E5 autonomous-commit deferred** (GDPR Art. 22). **#72 (auth double-write fix) intentionally excluded from the v2.13.0 tag** → `[Unreleased]`, next release. API #70+#71 + Web #112+#116 (+ deps #113). | **SDK 2.2.1 + Pro 2.8.0-pro** |
| **Platform v2.14.0 + Web v3.10.0-web "Typification P2c.1"** | **2026-06-22 (tagged + GH Release + cosign)** | **Per-tenant BYO LLM config** — each tenant brings its own provider + **encrypted** credentials, replacing the shared global key. Multi-provider pluggable (OpenAI-compatible / Azure OpenAI / Anthropic), resolved per-tenant at wrap-up, **fail-closed**; AI strictly **opt-in** ("no provider configured" = manual + deterministic typification, a valid state). `ILlmProviderResolver` + typed providers; `AddPlatformLlm` reshape keeps the global `ILlmProvider` for Flows. New `/admin/ai/llm-config` (masked key `keySet`/`keyLast4`, `typification:ai:configure`, `/test` probe) + Web config page; migration `009`; idempotent startup seed. **BREAKING (multi-tenant):** shared global LLM key retired; single-tenant/dev auto-seeded from appsettings. Folds **#72** auth-drain fix. Adversarial code review (11 agents, 2 majors) fixed a `/test` credential-exfil + a metrics-test parallel-pollution CI flake; Web `release.yml` hung once on QEMU arm64 (cancel+rerun, ~9 min). Deferred: **P2c.2** (platform-LLM metered/gated/billed) · **P2d** (voice, needs Pro) · **E5** (autonomous, GDPR Art. 22). API #74 + Web #117 + digests #34. | **SDK 2.2.1 + Pro 2.8.0-pro** |
| **Platform v2.14.1** | **2026-06-23 (tagged)** | Two fail-closed fixes (PR #78): management **impersonation** caller-id now uses the canonical `user_id ?? NameIdentifier ?? sub` (API-key callers were 403'd + audit-actor mis-attributed on revoke); **rate-limiter** moved after `TenantResolutionMiddleware` so `per-tenant` partitions stop collapsing to `__global__` (forward-looking — the policy isn't attached to a route yet). | **SDK 2.2.1 + Pro 2.8.0-pro** |
| **Platform v2.15.0 + Web v3.11.0-web "Typification P2c.2"** | **2026-06-24 (tagged + GH Release + cosign; digests #48)** | **Platform-managed metered LLM** — an **entitled** tenant uses a Verbara-operated LLM instead of BYO, **metered in AI Credits** (tokens ÷ configurable ratio), **gated** by new `PlanFeature.PlatformLlm` (Enterprise), **capped** by a monthly credit allowance via Billing (`Warn`/`SoftBlock`/`HardBlock`). `TenantLlmConfig.AiSource` (Byo/PlatformManaged); host-bound `PlatformLlmOptions` (operator key — never per-tenant/serialized/logged); `GET /admin/ai/credits`; migration `010`. **AI strictly opt-in; BYO unaffected + never metered.** Also fixed the flaky AuthWriteQueue drain test (causal barrier, PR #79). API PR #80 + Web C5 PR #132. | **SDK 2.2.1 + Pro 2.8.0-pro** |
| **Platform v2.16.0 "AI-credit ledger"** | **2026-06-28 (tagged + GH Release + cosign; api `sha256:b26a075f…`; digests verbara-website #49)** | Replaces the live-`SUM`-over-usage AI-credit accounting (v2.15.0) with a **signed append-only `ai_credit_ledger`** + O(1) `tenant_credit_balance` projection — the durable substrate for prepaid balances, top-ups, promo/partner-funded credits, postpaid overage, per-source reporting. **Inert at runtime until 2 default-off kill-switches are flipped** (prior `SUM` path byte-preserved). Program a→b→c1→c2 (PR #93/#95/#97/#99); **bundled the 4 standalone P2c.2 follow-ups** (entitlement re-check #83 · in/out pricing #86 · overage→dunning #88). FIFO multi-source draw Promo→Partner→Subscription/TopUp→PostPaid (`FOR UPDATE`, projection-locked-first); new RBAC `billing:credits:read`/`grant`; migrations `011/012/013`. Authoritative: **ADR-0033** (+Warn-overflow/(c)-split/(c2)-resolution addenda) + **ADR-0032**. | **SDK 2.2.1 + Pro 2.8.0-pro** |
| **Platform v2.17.0 "Typification E5 + audit integrity + CI/release hardening"** | **2026-07-06 (tagged)** | Reframed Typification **E5** autonomous-disposition enrichment (ADR-0034, **dark/OFF by default**) + audit-trail integrity follow-ups + AI-credit lazy-mint rollover fix + a report-only live-DB CI lane + a post-release image smoke harness + first `SECURITY.md`. Cascades **SDK 2.3.0** + **Pro 2.8.1-pro**. Migrations `014/015`. | **SDK 2.3.0 + Pro 2.8.1-pro** |
| **Platform v2.18.0 + Web v3.13.0-web "CSAT consumer (digital-first)"** | **2026-07-11** | **Platform (consumer) half of the CSAT Runner train** (ADR-0020). Brownfield extension of the existing Surveys domain: `survey_responses` +6 nullable CSAT columns + migration `016`; CSAT capture endpoints (`POST /api/v1/csat/responses/{webchat,email,sms}` + `GET /api/v1/analytics/csat/queues/{id}`) with a `LicenseFeature.CsatRunner` **402** gate; email IMAP gap-fill (`ImapInboundPoller` + `CsatReplyMailHandler`) + SMS correlator (24h window, most-recent-wins, non-rating fall-through); per-tenant template store + `ICsatTemplateProvider`; per-queue `CsatConfig` (4 `queue_configs` columns). **Hosts Pro's CSAT orchestrator (`IHostedService`) via 5 dependency-inverted seams** (`ICsatTemplateProvider` / `ICsatConversationSignal` / `ICsatEmailDispatcher` / `ICsatSmsDispatcher` / `ICsatConversationEndSource`). `GetByQueueAsync` `[Obsolete]` (removed v2.19.0). **Digital-first — voice/TTS deferred to a Pro Path-A follow-up** (`preview-voice` → 501); the typed `IPlatformHubClient.OnCsatResponseRecorded` Hub relay is a Pro follow-up (currently untyped `IHubContext` name-based fan-out). Runbook: `docs/operations/csat-runbook.md`. | **SDK 2.3.0 + Pro 2.9.0-pro** |

**Platform.Web** track sigue cadencia independiente desde el rebrand (Web v3.0.x; ROADMAP COMPLETE 2026-05-09 — todas las 7 niveles cerradas).

[Releases GitHub](https://github.com/verbara/Verbara.Platform/releases) · [Releases Web](https://github.com/verbara/Verbara.Platform.Web/releases)

---

## En curso (active plans)

> Las "En planificación" originales (1.9.x sub-releases + 2.0.0 arquitectónico + v2.1.0 image-binding + visibility flip) **YA SHIPPEARON** entre 2026-04-20 y 2026-05-10 — ver tabla "Shipped" arriba. Sección reescrita 2026-05-10 (post-flip) reflejando el estado real.

### 🟢 On `main`, sin release (2026-07-01) — Typification autonomous disposition (E5, reframed)
- **`typification-autonomous-disposition`** merged to `main` (PR #110 `407fd101`, archived #111) — the deferred **E5 autonomous-commit**, reframed by **[ADR-0034](decisions/0034-autonomous-typification-disposition.md)**. A grounding + framing pressure-test killed the original GDPR-Art.22 framing: the abandoned-wrap-up auto-close **already existed** (`ConversationTimeoutWorker`) so this is an **AI disposition enrichment** of that close (NO new worker), and **Art. 22 does not apply** to internal call-coding (tenant gate = controller instruction, not consent; audit is time-bounded, not append-only-forever; no data-subject dispute endpoint). Ships **dark** (`AutonomousDispositionEnabled` OFF; per-tenant opt-in + breaker + rate-cap required before prod). Migrations 014/015; new perm `typification:correct-autonomous`; AI-actor audit + Art.17 redaction; `Microsoft.OpenApi`→2.9.0 (NU1903). Living spec `openspec/specs/typification-autonomous-disposition` (8 reqs). **Enters a future Platform tag (currently unreleased; baseline is v2.16.0).** _Deferred follow-ups:_ α bulk human-confirmed review queue · β per-category autonomous whitelist · dispute-rate→calibration auto-pause · Platform.Web correction UI · the `RecordAudit` actorId claim-order class (see [[reference_typification_discovered_bugs]]).

### 🔧 On `main`, sin release — candidatos a v2.8.2 (2026-06-02)
- **Asterisk config-rendering hardening** (`677e7ef3`): los 4 confs con secretos (`ari/manager/res_pgsql/http`) ahora renderizan desde `*.conf.template` (versionado, placeholders) a `*.conf` **gitignored** (el entrypoint los regenera + inyecta secretos del env en cada boot). Elimina el working-tree sucio perpetuo + el riesgo de commitear secretos vivos; alinea los confs con el patrón `.env`/`.example`. k8s intacto (guard-on-template). **Editar el `.template`, nunca el `.conf`.** **DECISIÓN ABIERTA:** soltar como **v2.8.2** (toca solo entrypoint del image asterisk + templates) o dejar en `main`.
- **Backlog telefonía P3** (post-audit, no empezado): TLS/SRTP (`media_encryption` + `transport-tls`), resolver outbound Regex/DigitMask + `OverflowTrunkId` (hoy solo prefix), destinos DID más allá de cola (IVR/Flow/Agente). · **3B.3 voz:** supervisor monitor/whisper/barge + transfer atendida + conferencia.

### 🔥 Próximo ship: Platform v2.4.3 "Realtime startup migration hotfix" — Plan C activo, NO ejecutado (2026-05-23)

**Status verificado 2026-05-23 contra working tree de Platform `main`:**

| Indicador | Plan C target | Repo real | Hecho |
|---|---|---|---|
| `Directory.Build.props` | `<PackageVersion>2.4.3</PackageVersion>` | `2.4.2` | ❌ |
| `Chart.yaml` `version` / `appVersion` | `0.2.2` / `"2.4.3"` | `0.2.1` / `"2.3.1"` | ❌ |
| `values.yaml` `api.image.tag` | `v2.4.3` | `v2.3.1` | ❌ |
| `values.yaml` `realtime.image.tag` | `v2.4.3` | `v0.1.0-rc` | ❌ |
| `src/Verbara.Platform.Realtime/Program.cs` `EnsureSchemaAsync` call | presente | **ausente** | ❌ |
| Test `RealtimeStartupMigrationTests` | presente | dir no existe | ❌ |
| Git tag `v2.4.3` | existe | último tag = `v2.4.1` | ❌ |
| `authorized-digests.json` `v2.4.3` entry | `current[]` | último = `v2.4.2` | ❌ |
| **Plan C escrito y commiteado** | sí | sí (`305c36d6`) | ✅ |

**Scope del hotfix v2.4.3**: ONE-FILE source change en `src/Verbara.Platform.Realtime/Program.cs` — invocar `Verbara.Sdk.Cluster.Postgres.Migrations.MigrationRunner.EnsureSchemaAsync(...)` entre `var app = builder.Build()` y `app.UseAuthentication()`. Sin esa llamada el pod Realtime crash-loopea con `relation "cluster_distributed_lock" does not exist` (Gap-1 documentado en `docs/operations/phase-a5-smoke-test-2026-05-23.md`).

**3 plans en `docs/plans/active/`** se orquestan en orden:

1. [`2026-05-23-lab-migration-v2.3.1-to-v2.4.3.md`](plans/completed/2026-05-23-lab-migration-v2.3.1-to-v2.4.3.md) — **Plan C** lab migration (este). 7 fases C.0 → C.6 + rollback gates + 7 open questions para el maintainer.
2. [`2026-05-22-phase-a5-cluster-leader-election.md`](plans/completed/2026-05-22-phase-a5-cluster-leader-election.md) — **Plan A** consume `LeaderElectionService` en Realtime/Cluster hot paths.
3. [`2026-05-23-phase-a5-talos-smoke-test.md`](plans/completed/2026-05-23-phase-a5-talos-smoke-test.md) — **Plan B** Talos K8s smoke test (leader failover + zero-duplicate-SignalR-delivery) — se ejecuta inmediatamente después de C.6 PASS.

**⚠️ Plan C necesita REVISIÓN antes de ejecutar** — fue redactado el 2026-05-23 AM, antes de que mergearas PR #5 (tarde 2026-05-23) + PR #6 (mismo día):

- **PR #5** (`c21c8a97` + `756f243f` + `cabc1e55`): `release.yml` ahora **obligatorio** para builds que no sean `RETROACTIVE-TAG`. Plan C §4.5 paso 3-7 hace `docker build` + `cosign sign` manuales (path bypass-release.yml). DEBE ajustarse a una de dos rutas: (a) re-escribir §4.5 para usar `gh workflow run release.yml -f tag=v2.4.3`, o (b) anotar el tag con `RETROACTIVE-TAG: <razón>` para que `release.yml` se skipée + manualmente registrar digest en `authorized-digests.json`.
- **PR #6** (`c60c3c53` + `82862310` + `d09b70ca`): cosign bumped **v2.5.2 → v3.0.6** + `sigstore/cosign-installer@v3 → @v4.1.2`. Plan C §4.5 paso 6-7 comandos `cosign sign` / `cosign verify` deben asumir v3 (sin `--insecure-ignore-tlog` legacy flag — verificar sintaxis v3).

**ADR-0024 sigue pendiente** — canonicalizaría el contrato cosign-verify-or-RETROACTIVE-TAG. Idealmente file antes de ejecutar Plan C para que el path tomado quede grabado.

**Acceptance criteria** (§9 de Plan C): 15 check-boxes. Cuando todos verdes → Plan B arranca → si Plan B pasa → Phase A.5 CLOSED → ADR-0022 track FULLY CLOSED → R5.5 Phase B-LK desbloqueado.

---

### SMB Reference Deployment + Manuales — ✅ Fase 1 COMPLETE 2026-05-17

**Contexto.** Tras el visibility flip (2026-05-10), el producto necesitaba pivotar desde validación de producción interna (R5.5) hacia entregables customer-facing. Decisión 2026-05-17: priorizar **reference deployment + manuales paso a paso** para que un cliente final (o equipo de implementación) pueda instalar Verbara desde cero y configurar los 3 canales V1 (Voz/SIP + WebChat + Email) sin acompañamiento del equipo Verbara.

**Fase 1 = Docker SMB on-premise.** Fase 2 (K8s on-prem) deferred — el chart actual asume el lab Talos y necesita refactor (parametrizar Kamailio IP, externalizar secrets, cert-manager) que toma 2-3 semanas adicionales.

**Plan canónico:** `docs/plans/completed/2026-05-17-reference-deployment-smb.md` (archivado al cierre de Fase 1). Decisión + tiers de hardware + 4 escenarios NAT + decisión `network_mode: host` en lugar de bridge — todo argumentado ahí.

**Deliverables shipped (6 commits)**:
1. **Imagen Web publicada** (`ghcr.io/verbara/platform/web:v3.0.3-web`) — workflow GitHub Actions mirror del API, firmada con cosign keypair compartido. Antes solo existía la imagen API públicamente; ahora ambas pueden verificarse con el mismo `cosign.pub`.
2. **`docker/docker-compose.reference-smb.yml`** + **`.env.reference-smb.example`** (~90 vars) + **`docker-compose.coturn.yml`** overlay. Arquitectura: Asterisk `network_mode: host` (todos los puertos SIP/RTP en el host NIC — evita ~3 GB RAM de docker-proxy overhead para 300 calls), Platform.Api + Web + Postgres + Redis en bridge, Postgres bind loopback `127.0.0.1:5432` para que Asterisk-on-host alcance realtime DB. Tier matrix Lite (50 calls) / Standard (150) / Plus (300) configurable via env. Pinned a `api:v2.1.0` + `web:v3.0.3-web`. Renderer/Mail build local (no publicados a ghcr aún). `entrypoint-asterisk.sh` extendido con `PG_REALTIME_HOST/PORT/DB/USER/PASSWORD` env substitution.
3. **`scripts/quickstart-smb.sh`** — 11 pre-flight checks: tooling, recursos RAM/CPU/disk con detección de tier, puertos TCP+UDP libres, rango RTP completo (`ss -uln` scan), firewall hints por distro (UFW/firewalld/nftables) + cloud metadata detection (GCP/AWS/Azure), **detección NAT 4 escenarios** (A IP directa, B/C privada+NAT, D CGNAT), bandwidth informativo, `.env` validation (rechaza placeholders `CHANGE_ME`), pull + up --wait + polling `/health/ready`. Validado en el host dev (detectó correctamente escenario C: `192.168.40.100 LAN → 200.118.42.61 pública`).
4. **12 manuales `docs/manuales/smb/`** (~3,800 líneas, 100% español): `00-vision-general` (arquitectura + tiers + OS), `01-instalacion-docker` (install por distro Debian/Ubuntu/Rocky/Alma/Amazon Linux + firewall por distro + cloud SG + on-prem router commands MikroTik/pfSense/Ubiquiti/TP-Link + DNS + Let's Encrypt + 4 escenarios NAT), `02-arranque-stack`, `03-setup-inicial` (admin + tenant + agente + queue), `04-canal-webchat`, `05-canal-email` (SMTP/IMAP + MS Graph OAuth + Gmail OAuth2), `06-canal-voz-sip` (538 líneas — el más extenso: trunk Twilio Elastic + genérico, inbound dialplan 3 patrones, WebRTC agent provisioning, golden path test, Coturn behind strict NAT, escalado entre tiers), `07-validacion-e2e`, `08-troubleshooting-sip` (10 secciones síntoma→causa→solución), `99-troubleshooting` (no-SIP), `checklist-validacion-cliente` (imprimible con firmas), `capacity-reference` (tier matrix + mediciones reales del D-L 24h soak).
5. **`tests/e2e/tests/reference-deployment.spec.ts`** + **`fixtures/channel.fixture.ts`** en Verbara.Platform.Web — 7 tests con tag `@reference-deployment`: stack healthcheck, setup wizard completo, WebChat demo widget loads, WebChat config persistence, Email config persistence, Voice trunk CRUD + WebRTC provisioning, Asterisk ARI smoke (skip si no hay `VERBARA_ARI_PASSWORD`).
6. **3 helpers de validación**: `tests/manual-validation/webchat-test.html` (página standalone con widget embedded + form configurable + checklist 10 items), `sip-softphone-test.md` (guía Linphone + Zoiper + WebRTC comparativa para test sin trunk), `scripts/capacity-calc.sh` (calc pre-deploy: agents + codec + retention → tier + hardware + .env vars + cloud costs orientativos AWS/Azure/GCP/Hetzner/DO).

**Total:** ~6,300 líneas customer-facing en 1 día. Sin tocar `infra/k8s/helm/` (Fase 2). Sin romper compose anteriores (`smb.yml`/`full.yml`/`production.yml`/`scale.yml`/`demo.yml` siguen funcionando idénticos).

**Fase 2 (deferred sin fecha)** — K8s on-prem reference: refactor Helm chart (externalizar secrets, eliminar `192.168.122.201` hardcoded, parametrizar hostnames, cert-manager integration, Kamailio off-hostNetwork con MetalLB), Docker → K8s migration guide, manuales K8s espejo de los SMB.

### ADR-0018 visibility-flip checklist — ✅ COMPLETE 2026-05-10

7/7 GREEN. Flip ejecutado 2026-05-10 19:04 UTC sobre Platform + Platform.Web. Plan tracking en `docs/plans/completed/2026-05-08-visibility-decision-and-alignment.md`. Apache 2.0 economics ya operando; ADR-0006 funnel ahora viable.

### R5.5 K8s Phase 0LK reabierto (2026-05-16) — chart gap-fix blocking B-LK

Status: **2 gaps reales detectados, 1 mitigado, 1 pendiente de ship.**

Cuando se reanudó B-LK.1 el 2026-05-16, primer `curl -X POST /api/v1/setup` contra el cluster devolvió `HTTP 401 — Access to the path '/app/data' is denied`. Diagnóstico profundo (ver `~/.claude/projects/-media-Data-Source-Verbara-Verbara-Platform/memory/project_r55_k8s_real_state_2026_05_16.md` — incluye análisis de 7 alternativas rechazadas) reveló que Phase 0LK declaró "FULLY COMPLETE" con 28 pods Ready, pero **el path de auth nunca se ejerció contra el cluster K8s**. Hay DOS gaps en el Helm chart `infra/k8s/helm/platform/`:

1. **Orphaned legacy `Ingress`** (`templates/ingress.yaml`) — Cilium tiene `gatewayAPI.enabled: true` pero NO `ingressController.enabled: true`, así que los recursos `networking.k8s.io/v1 Ingress` quedan huérfanos sin controller. El Gateway cluster-level tiene 0 HTTPRoutes. **Mitigado hoy** con `infra/k8s/manifests/httproute-platform.yaml` (HTTPRoutes binding `api.r55.local` → `platform-api:5000` + `r55.local` → `web:80`, cross-ns referencia al `default/platform-gateway`). Sin commitear. Migrar chart de Ingress → HTTPRoute es la solución permanente.

2. **ADR-0012 JWT rotation pool sin wireup** — `Program.cs:540` defaults `Auth:KeyDirectory` a `{ContentRoot}/data` = `/app/data`. Pod corre como UID 1654 sin volumen ahí → 401 en TODA llamada de auth. Aunque se montase un volumen escribible, el problema multi-réplica que ADR-0012 fue escrita para resolver persiste (cada réplica genera su propio RSA → JWT firmado por A falla validación en B). **El chart no setea las 3 env vars que activan el path correcto:**
   ```yaml
   - name: Identity__JwtKeyRotation__UseRotationPool
     value: "true"
   - name: Identity__JwtKeyRotation__RequireRedisStore
     value: "true"
   - name: ConnectionStrings__IdentityRedis
     value: "redis.r55-data.svc.cluster.local:6379"
   ```
   Redis prereqs OK: service `redis.r55-data.svc:6379` Running 12d; NetworkPolicy `allow-redis-from-platform` permite 6379 desde r55-platform; conectividad TCP verificada via `/dev/tcp` desde dentro del pod platform-api.

**Atajos rechazados** (todos crean nuevo anti-pattern en lugar de cerrar el real): emptyDir + fsGroup (claves efímeras → logout en cada redeploy + multi-replica desync), PVC + fsGroup (pods → pets, rompe HPA 2→8), image rebuild con /app/data chowned (single-pod-ok, multi-replica-broken), initContainer chown (idem), runAsRoot (viola PodSecurity baseline). Sólo la opción 1 (Redis-pool wireup) cierra ambos síntomas.

**Plan de ship** (~30 min):
- Editar `infra/k8s/helm/platform/templates/platform-api-deployment.yaml` + `values.yaml` (3 env vars + matching values block).
- `helm upgrade platform infra/k8s/helm/platform/` (rolling, ~60 s).
- Probar `/api/v1/setup` → 201 (o 409); seed-staging.sh; arrancar B-LK.1.
- Commit conjunto: `feat(k8s): R5.5 Phase 0LK gap-fix (HTTPRoute migration + JWT rotation pool wireup) — unblocks B-LK auth`.

**Notas adicionales descubiertas en el camino:**
- `scripts/k8s-up.sh` necesita rama "warm-restart": pasos 5 (apply-config --insecure) y 6 (bootstrap) fallan si los nodos ya están provisionados. Hoy hubo que ejecutar manualmente `talosctl kubeconfig --force` para saltarlos. Parche idempotente a `net-start default` ya aplicado en working tree (no committeado).
- Imagen deployada es `asterisk-platform/api:1.14.6` + `asterisk-platform/web:1.15.5` (pre-rebrand, namespace OCI `asterisk-platform/*`) — esto es **metodológicamente correcto** para Phase B-LK ya que matchea la baseline Docker B-L también v1.14.6 (D-L 24h soak PASS 2026-04-30 fue en esta versión). NO actualizar a v2.1.0 antes de B-LK o el comparativo K8s vs Docker pierde validez. SDK pin transitivo ~1.15.x, Pro pin transitivo 1.16.0-pro.
- **Registry host `192.168.122.1:5050` DOWN** — los pods corren porque las imágenes están cacheadas en `containerd` de cada nodo. `helm upgrade` con `imagePullPolicy: IfNotPresent` (chart default, verificar) funciona; si fuera `Always` fallaría. **Antes de Phase C-LK chaos** (que mata pods intencionalmente) el registry debe estar arriba. Para B-LK.1 baseline es safe diferirlo.
- **Licensing no se necesita para B-LK** — chart hardcodea `licensing.enforcementMode: Disabled`. `LicenseGateMiddleware` pasa todo sin validar. Mantiene paridad con Docker B-L baseline (también sin license). Modos disponibles: `Disabled` (actual), `WarnOnly` (staging), `Enforce` (production — requeriría license key con `AuthorizedImageDigests` si fuera v2.1.0 por Pro/ADR-0011, o solo expiry check si es v1.14.6). Medir overhead de `LicenseGateMiddleware` con Enforce + license válido sería un Phase B-LK.6 opcional separado.

### Post-flip follow-ups (no version-gated)

Capturados en el Status update del 2026-05-10 en [ADR-0018](decisions/0018-visibility-decision-3-private-now-public-on-trigger.md). Resumen:
- **ghcr.io package visibility** — los paquetes OCI (`ghcr.io/verbara/platform/{api,web}`) heredan visibility privada por defecto; flip manual UI pendiente (GitHub no expone REST API para esto).
- **Real-customer e2e validation** — primer cliente Tier 1+ que descargue imagen + corra `verbara-verify-image.sh` cerrará la valid-loop completa.
- **Announcement** (HN "Show HN" / r/asterisk / r/devops / Twitter / ProductHunt) — deferred hasta primer customer-driven milestone.
- **Cloudflare git auto-deploy reconnect** — re-attach pipeline (operator: dashboard → Workers & Pages → verbara-website → Settings → Builds & deployments).

### Platform v2.1.x patch train (reactive)

Reservado para parches sobre v2.1.0 (security follow-ups, image-binding bug fixes, cosign workflow drift). Sin plan activo; trigger será findings reales en producción o feedback de operadores tras visibility flip.

### Platform v2.2.0 "Pro v2.4.0-pro consumer migration" — ✅ SHIPPED 2026-05-18

**Status:** **SHIPPED** — commit `0de22761` on `origin/main`, tag `v2.2.0` published, Platform CI `release.yml` running (cosign + GHCR signed image). See Shipped table above for the full row. Detailed scope retained below for historical reference; section moves to historical/archived in next roadmap revision.

**Scope (as shipped):**
- **Pro pin cascade** — 21 `Verbara.Sdk.Pro.*` `PackageVersion` lines en `Directory.Packages.props` bumpean `2.3.0-pro` → `2.4.0-pro`.
- **Nuevo endpoint** `GET /management/system/license/status` (`PlatformAdminOnly`, sibling del existente `GET /management/system/license`) consumiendo el nuevo `ILicenseStatusReader` de Pro 2.4.0-pro. Retorna raw `LicenseStatusSnapshot` (NO Platform DTO wrapper — Pro guarantees el contract vía `LicensingJsonContext`). Tier, ExpiresAt, MaxAgents, MaxNodes, AuthorizedDigestsCount, LastValidationResult, LastValidationAt, RevalidationInterval, Licensee.
- **`LicenseGateMiddleware` contract change** — HTTP status code **403 → 402 Payment Required** (RFC-9110-accurate para subscription gates, no consume SLO error budget, no auto-retry en HTTP clients). Body sigue siendo RFC 9457 ProblemDetails pero gana extension members `tier_required` + `trial_url` + `upgrade_url` + `contact_sales_url` poblados por `LicenseGuard.Evaluate` (Pro 2.4.0-pro's `Enrich` helper).
- **SMB operator docs migration off `LICENSING_MODE`** — 4 files updated (`.env.reference-smb.example`, `docker-compose.reference-smb.yml`, `docs/manuales/smb/99-troubleshooting.md`, `docs/operations/first-realistic-demo.md`). Reemplazo canónico: `LICENSE_PATH=` + referencia al Tier 0.5 issuer en `https://verbara.io/developer-license`. Historical docs (specs/, completed plans, R5.5 active plan, onboarding-feedback) intacto — snapshots-in-time.
- **Dev/demo compose strategy** — `docker-compose.full.yml` + `demo/docker-compose.demo.yml` MANTIENEN `Licensing__EnforcementMode: Disabled` (removing-the-var rompería startup bajo Pro 2.4.0-pro back-compat) PERO añaden `Logging__LogLevel__Verbara.Sdk.Pro.Licensing.LicensingDeprecationHostedService: Warning` para suprimir el event-id 12001 boot log spam hasta lockstep migration en v2.5.0-pro.
- **Helm chart additive update** — `infra/k8s/helm/platform/values.yaml` gana `licenseFilePath: ""` (nuevo, opcional). `enforcementMode` mantenido pero documentado como deprecated. Template emite ambas env vars condicionalmente.
- **CHANGELOG** entry `## [2.2.0] — 2026-05-17` cubriendo Pro pin cascade + endpoint + contract change + deprecation handling + SMB doc updates.
- **Tests** — `LicenseGateTests.cs` flip 403→402 + rename 3 métodos + 4 nuevos tests (ProblemDetails extension members per `LicenseBlockReason`). `ManagementClusterLicenseGateTests.cs` add `.NotBe(PaymentRequired)`. NEW `LicenseStatusEndpointTests.cs` (4 tests: happy-path / unloaded / 401-anon / 403-tenant-admin).

**Decisiones arquitectónicas tomadas en planning** (algunas divergen del spec Pro original — Platform-side prerogative):

| Aspect | Pro spec ejemplo | v2.2.0 decision | Why |
|---|---|---|---|
| Endpoint path | `/api/v1/admin/license/status` | **`/management/system/license/status`** | Platform convention — system-level state pertenece a `/management/system/*` (`PlatformAdminOnly`), no a tenant-admin `/api/v1/admin/*` (`AdminOnly`) |
| HTTP status code | 501 Not Implemented | **402 Payment Required** | RFC-9110-accurate para subscription gates (Stripe-style); 4xx no consume SLO error budget; clients no auto-retry 402 |
| Endpoint return shape | (custom JSON) | **Raw `LicenseStatusSnapshot`** (no Platform DTO) | Pro guarantees contract via `LicensingJsonContext`; wrapping = translation layer con zero value |

**Total: 16 files modified.** ~6h estimated. Target tag `v2.2.0`.

**Pre-flight risk (resolved during ship):** `NuGet.Config` `packageSourceMapping` constraint para `Verbara.Sdk.Pro*` → GH Packages source. Pro CI broken (0 workflow runs en `verbara/Verbara.Sdk.Pro` desde rebrand 2026-05-05). **Resolution:** maintainer's `GITHUB_PACKAGES_PAT` (`~/.verbara/secrets.env`) used to push all 24 v2.4.0-pro `.nupkg` files manually via `dotnet nuget push --source github`. Platform restore resolved cleanly.

**Cross-repo unlock state (post-ship 2026-05-18):** ✅ Pro v2.5.0-pro pre-conditions #2 (consumer release) + #3 (manuales updated) **MET**. ⏳ Pre-condition #1 (≥6 weeks since Pro v2.4.0-pro tag 2026-05-17) elegible **2026-06-28+** — only calendar gate remaining.

### Worker Resilience Pattern Hardening — Pro v2.4.1-pro + Platform v2.3.0 — ✅ SHIPPED 2026-05-18 (commits `8d98022` Pro, `5d773e55` + `d592e8d5` Platform; tags `v2.4.1-pro` + `v2.3.0`)

**Status:** SHIPPED. 27 workers hardened cross-repo (13 Pro + 14 Platform) + `BackgroundServiceExceptionBehavior.StopHost` wired in `Verbara.Platform.Api/Program.cs`. 48 new resilience tests (25 Pro Tier-1 deep + smoke + 23 Platform incl. integration test for HostOptions wiring). Pro 1,550 tests pass; Platform 961 tests pass (was 938 + 23 new). 0 warnings; AOT publish clean.

**D-LK soak repeat — bundled with Pro v2.5.0-pro train, NOT run standalone:**

The post-train D-LK rerun (originally tracked as "validate hardening in real cluster") is consolidated into the Pro v2.5.0-pro release validation when that train executes (≥2026-06-28). Rationale: (a) 48 new tests already lock the discipline at code-level; (b) `StopHost` is well-tested .NET runtime behavior; (c) workers running live in lab K8s during the 6-week observability window IS continuous validation — any silent-death regression surfaces as visible pod restart via the new discipline; (d) bundling saves cluster-hours + improves failure attribution; (e) the v2.5.0-pro behavioral change (Pro features degrade to 402 instead of crashing at startup on invalid license) introduces new scenarios that benefit from sustained soak validation in the same run.

**Extended D-LK protocol** when v2.5.0-pro train runs (5 scenarios):

| ID | Scenario | Validates v2.4.1-pro hardening | Validates v2.5.0-pro removal |
|---|---|---|---|
| A | Boot with valid license + sustained 24h load | ✅ no silent worker death | ✅ Pro features OK |
| B | Boot WITHOUT license (v2.5.0-pro: app starts) | indirect | ✅ Pro features 402 from boot |
| C | Mid-soak license expiration at T+12h | ✅ no worker crash during transition | ✅ in-flight 200→402 graceful |
| D | Chaos: kill worker under stress → pod restart visible | ✅ `StopHost` E2E + liveness reaction | n/a |
| E | Chaos: invalid IMAGE_DIGEST + license válida | n/a | ✅ `UnauthorizedImage` 402 path |

Canonical: Pro spec [`2026-05-17-pro-v250-licensing-enforcement-mode-removal.md`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/specs/2026-05-17-pro-v250-licensing-enforcement-mode-removal.md) section "Validation requirements — D-LK protocol extension".

---

### Worker Resilience Pattern Hardening — ORIGINAL planning (kept for archive — superseded by ✅ SHIPPED above)

**Status:** **Spec listo, ejecución pending.** Spec canónico: [`docs/specs/2026-05-18-worker-resilience-pattern-hardening.md`](specs/2026-05-18-worker-resilience-pattern-hardening.md).

**Origen:** D-LK 24h soak forensics (2026-05-18) — ver `R5.5 K8s Phase D-LK` arriba. El Finding 1 documentó un pattern bug que afecta cualquier `BackgroundService` long-running en Platform o Pro: cuando una excepción propaga out of `ExecuteAsync`, el default `BackgroundServiceExceptionBehavior.Ignore` la oculta — el worker queda "Running" para el orquestador pero internamente muerto. K8s liveness probe detecta la cascada (health check Unhealthy) y mata el pod 30-45s después; Docker no tiene esa autorrecuperación y el worker queda zombie indefinidamente.

**Scope (cross-repo):**

- **Pattern A (timer-based workers):** outer try-catch wrapping el while loop completo + `LogWorkerCrash` + `throw` (rethrow para que `StopHost` lo capture).
- **Pattern B (Rx event-driven workers):** OnError handler nullifica `_subscription` → `CheckHealthAsync` retorna Unhealthy en lugar de Healthy-stale.
- **Host-level:** `services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost)` en `Program.cs`. **Esto es el cambio crítico** que hace el pattern funcione.

**Workers afectados (audit needed):**

| Repo | Worker | Pattern |
|---|---|---|
| Platform | `QueueDistributionWorker` | A — confirmed vulnerable (el que murió en D-LK) |
| Platform | `ConversationTimeoutWorker` | A — likely same pattern |
| Platform | Otros `*Worker.cs` en `src/Verbara.Platform.Api/Services/` | audit |
| Pro | `PresenceFanoutService` + `PresenceMergeConsumer` | B |
| Pro | `Verbara.Sdk.Pro.Dialer/` workers | audit (A/B) |
| Pro | `Verbara.Sdk.Pro.EventStore/` workers | audit (A/B) |
| Pro | `Verbara.Sdk.Pro.Realtime/` workers | audit (A/B) |

Estimado 8-12 worker files total cross-repo.

**Cadence:**

- **Pro v2.4.1-pro** — Pro half (~10h: PresenceFanout/Merge + Dialer/EventStore/Realtime workers audit + tests). Spec section "Pattern B" + "Pro repo" en el doc.
- **Platform v2.3.0** — Platform half (~10h: QueueDistributionWorker + ConversationTimeoutWorker + HostOptions wiring + integration test + Platform-side `WorkerLog.cs`). Sibling release; consumer migration de Pro v2.4.1-pro.
- **Sequence:** ship Pro v2.4.1-pro primero, luego Platform v2.3.0 que consume.

**Decisión: NO bundlear con Pro v2.4.0-pro Licensing simplification** (que YA shipped 2026-05-17). Razones: clarity > bundle (rollback de un train no afecta el otro; tests aislados; changelog limpio). Confirmado en spec §"Distribution / release pathway".

**Relacionado:** el Phase G-PRE del Pro v2.4.0-pro spec (presence health check semantic fix — `CheckHealthAsync` differentiates pre-start / disposed / active-idle) **NO shipped en v2.4.0-pro** (no estaba en spec original); ahora documentado para bundlear con Pro v2.4.1-pro Worker Resilience train. Total Pro v2.4.1-pro effort: ~10h + 3h G-PRE = ~13h.

### R5.5 K8s Phase D-LK — ✅ COMPLETE 2026-05-18 (PASS-with-findings)

**Status:** ✅ **PASS** del 24h soak K8s. 2,591,667 / 2,592,000 requests OK (**99.987%** success). p99 latency **10.73 ms** (9.3× bajo el SLO budget de 100 ms). Run window: 2026-05-17 04:36:49 → 2026-05-18 04:36:13 local. Reporte canónico: [`docs/operations/soak-test-report-k8s-local.md`](operations/soak-test-report-k8s-local.md).

**El "OOM" hipotético resultó NO ser OOM.** Forensics post-soak ([`chaos-reports/dlk-oom-analysis-20260518.md`](../chaos-reports/dlk-oom-analysis-20260518.md)) reveló que el restart a T+16h36m fue K8s liveness probe failure (NOT kernel OOM killer) gatillado por `QueueDistributionWorker` silent death. 333 fails (0.013%) correspondieron al window de SIGTERM→SIGKILL grace + Cilium endpoint slice update (~30s).

**5 hallazgos catalogados:**
1. 🔴 **Worker silent-death architectural bug** (REAL, customer-facing). `BackgroundService` workers en Platform + Pro pueden morir silenciosamente cuando excepción propaga out of `ExecuteAsync` con default `BackgroundServiceExceptionBehavior.Ignore`. Afecta cualquier deploy long-running (Docker SMB o K8s). Nuevo spec: [`docs/specs/2026-05-18-worker-resilience-pattern-hardening.md`](specs/2026-05-18-worker-resilience-pattern-hardening.md). Ships en Worker Resilience track (sección siguiente).
2. 🟡 **`presence-fanout/merge` Degraded 21h FALSE POSITIVE.** Workers son event-driven (Rx subscription); idle heartbeat = stale by design. El bug está en el HealthCheck design, no en los workers. Cosmético. Fix bundleado en Pro v2.4.0-pro Phase G-PRE (ya en spec, ejecución pendiente patch o v2.4.1).
3. 🟡 **metrics-server caído en lab Talos** → HPA non-functional. Mandatory para Fase 2 K8s ref deploy customer-facing. ~2h.
4. 🟢 **Cilium eBPF endpoint slice updates ROBUST** (validation). Production-ready.
5. 🟡 **Chaos Mesh + Cilium kube-proxy-replacement incompat** reconfirmado. Deferred Phase 0C cloud.

**Comparativa con Docker D-L (2026-04-30 PASS):** K8s D-LK p99 10.73 ms vs Docker D-L p99 60.66 ms (K8s ~6× mejor, pero scenario más simple). Restart pattern absent en Docker (no liveness probe mechanism). Methodologically válido — D-L baseline cerró antes de pivot a customer-facing trabajo.

**R5.5 K8s validation completa.** Próximos K8s steps (Fase 2 customer ref deploy, cloud Phase 0C+) deferidos por pivot 2026-05-17 a SMB Docker.

### Platform v3.0.0 "ADR-0019 wildcard removal" (planned, breaking)

Próximo major. Removes:
- Legacy `platform:*` wildcard en management API keys (operadores deben rotar a scope-whitelists explícitos).
- Posiblemente otras deprecations acumuladas en v2.x.

Sin fecha — gated por adopción del scope-aware pattern por consumidores existentes (none today post-flip; deprecation warning ya en v2.1.0).

---

## Deferred / backlog (sin versión asignada)

### Security & Auth
- **Trusted devices** — device fingerprinting + MFA bypass 30d (diferido de Sub C).
- **Session LastActivityAt** tracking (diferido de Sub C) — middleware hook + storage write cost.
- **Tenant session-timeout UI display** (diferido de Sub C) — fields existen backend, no expuestos en frontend.
- **Self-service admin-lockdown policies** — `DisableSelfServiceMfaChanges`, `DisableSelfServicePasswordChange` tenant flags.

### Ops & Infra
- SLA breach alerting + idle agent detection (diferido de Sprint 4).
- TenantBranding font + login bg image (diferido de Sprint 4).
- Payment gateway integration (Stripe) — fuera de v1.x.
- Notification escalation chains + AI-driven alerts + auto-resolve.
- Push notifications (mobile/PWA) + SMS notification channel.

### Platform.Web E2E testing
- Sprint 4 — Agent Workspace (~40 tests).
- Sprint 5 — Advanced Flows (~50 tests).
- Sprint 6 — Cross-Cutting (~30 tests).

---

## Principios de planificación

1. **Platform sigue a Pro.** Platform no adelanta features que requieran capacidades Pro no-shippeadas. El versionado Platform es downstream del Pro.
2. **Web y API mismo número.** Platform 1.8.0 ↔ Platform.Web 1.8.0. Desincronizar genera confusión en deploy coordinados.
3. **0 breaking changes en minors.** Same rule que Pro. Major 2.0 cuando haya breaking acumulado.
4. **Plans cross-repo aquí.** Cualquier plan que toque API + Web se origina en este repo's `docs/plans/active/`. Platform.Web repo's `docs/` es secundario.
5. **AOT-first en API, React 19 + Zustand en Web.** Sin deviations.

---

## Cadencia histórica y proyectada

- **Platform 1.0→1.8.0** (foundation, 2026-03 → 2026-04-19): 8 minors en ~4 semanas (feature-landing agresivo, coordinado con SDK/Pro).
- **Platform 1.9.0→1.13.0** (R3 + R5 release trains, 2026-04-20 → 2026-04-26): 5 minors en ~6 días (hardening + production-validation cadence).
- **Platform 1.14.0→1.14.6** (AHH + ADR-0015 patch train, 2026-04-27 → 2026-04-28): 7 patches en 2 días (hot fixes + pool sprawl mitigation).
- **Platform 1.15.0→2.0.0** (rebrand window, 2026-05-02 → 2026-05-05): pre-rebrand foundation + Verbara cutover.
- **Platform 2.0.0→2.0.1** (post-rebrand security patch, 2026-05-05 → 2026-05-10): 1 patch closing ADR-0018 Trigger 3 P0+P1 findings.
- **Platform 2.1.0** (2026-05-10, same-day with 2.0.1): image-binding + cosign-signed image + Helm/docker-compose verification toolkit + visibility-flip event. Final trigger (#5) cerrado y flip ejecutado en single coordinated push.
- **Platform 3.0.0** (planned, breaking): ADR-0019 wildcard removal + accumulated v2.x deprecations.
