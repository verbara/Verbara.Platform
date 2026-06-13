# CLAUDE.md

Project context for Claude Code working on **Verbara.Platform** — the API host and composition root for an omnichannel contact-center built on .NET 10. Read top-to-bottom for architecture; jump to **Critical Gotchas** for non-obvious pitfalls before editing code.

> **🔒 AOT shipping — Native AOT achieved ([ADR-0022](docs/decisions/0022-platform-api-aot-shipping-path.md), Phase D shipped 2026-05-20):** `Verbara.Platform.Api` publishes **Native AOT** (`<IsAotCompatible>true>`; 0 `IL2026`/`IL3050`/`IL207x` diagnostics; native ELF, 0 managed Verbara DLLs). Phases A (SignalR Hub → `Verbara.Platform.Realtime`) + B (EF Core DataProtection → Dapper, then raw Npgsql) + C (empirical: Dapper was the last blocker) + **D (total Dapper removal cross-repo → `Verbara.Sdk.Data.Npgsql` facade)** are all closed. Validated end-to-end: the native binary boots + serves real HTTP (0 JSON 500s) with the migrated Npgsql data layer running under AOT — see `docs/operations/phase-d-validation/2026-05-19-pilot-aot-delta.md`.
> - **HARD CONSTRAINT:** Dapper / Dapper.AOT / `Verbara.Sdk.Dapper.Stubs` are **permanently banned** — a `BanDapperPackageReferences` guard in `Directory.Build.props` fails the build if any is referenced. Use `Verbara.Sdk.Data.Npgsql`. `Verbara.Platform.Api` also sets `<JsonSerializerIsReflectionEnabledByDefault>false>`: every (de)serialized DTO MUST be in a `[JsonSerializable]` source-gen context (`ApiJsonContext` / `RealtimeContractsJsonContext`).
> - **Release activities — ✅ ALL DONE (gate CLOSED 2026-05-22):** Phase 5 cutover shipped (SDK 2.2.0 → nuget.org, Pro 2.5.0-pro → GitHub Packages, Platform v2.4.0 → **v2.4.1** = 4 cosign-signed images on ghcr.io per ADR-0023, digests authorized). Phase 6 — 24h AOT soak — **PASSED** against `ghcr.io/verbara/platform/api:v2.4.1` (803M req, 0 fail, p99 25 ms, no leak, pg_conns 11 flat, 0 restart). Evidence committed at [`tests/Verbara.Platform.LoadTests/soak-reports/soak-aot-v241-24h-20260521-1201.log`](tests/Verbara.Platform.LoadTests/soak-reports/soak-aot-v241-24h-20260521-1201.log) + [`soak-24h-drift-20260521-1201.log`](tests/Verbara.Platform.LoadTests/soak-reports/soak-24h-drift-20260521-1201.log) + [`drift-logger.sh`](tests/Verbara.Platform.LoadTests/soak-reports/drift-logger.sh) (commit `ab7925fb`). The root `Dockerfile` is the AOT pathway (`runtime-deps` final stage); a Docker AOT build restores Pro from `github`. Soak relaunch procedure + lab gotchas: memory `reference_local_infra_gotchas.md`.
> - **Release process hardening (2026-05-23 PR #5 + PR #6):** After the v2.4.2 anomaly (4 ghcr.io images pushed via maintainer-local `docker buildx --push`, bypassing `release.yml` — visibility-monitor stayed green for 13h because it only checked anonymous pull, not cosign), `visibility-monitor.yml` now also `cosign verify`s the 5 tagged images + checks `https://verbara.io/keys/cosign.pub` PEM-block parity vs in-repo `.github/cosign.pub`; new `digest-reconciliation.yml` reconciles `verbara-website/data/authorized-digests.json` against ghcr.io daily 07:00 UTC; `release.yml` skips cleanly when the annotated tag message carries `RETROACTIVE-TAG`. **PR #6 cascade fix (2026-05-23 evening)**: cosign bumped **v2.5.2 → v3.0.6** + `sigstore/cosign-installer@v3 → @v4.1.2` across all 3 workflows (resolved v2.4.2 verify failure since v2.5.2 couldn't validate certain newer signature formats). **ADR-0024 still pending file.** Implication for v2.4.3 ship (lab migration Plan C in `docs/plans/active/`): **MUST go through `release.yml`** (not buildx-direct, or daily reconciliation fires drift) AND use cosign v3 syntax (`--insecure-ignore-tlog` flag handling changed between v2 and v3).
> - **Phase A.5 Platform consumption — ✅ CLOSED 2026-05-24:** 12 PRs (#18-30) + 6 cosign-signed image releases (v2.4.4 → v2.4.5 → v2.4.6 → v2.4.7 → v2.5.0 → **v2.5.1**) closed Plan B Test 5 (SignalR exactly-once delivery) **PARTIAL → PASS**. 7-layer escalation chain resolved: leader-election scaffold + Realtime audit endpoint (PR #18) + harness walking skeleton (PR #19) + chart-only Redis/IdentityRedis env wiring (PRs #22-23) + API `sub`-claim fallback in 4 endpoints (PR #24) + Core↔Pro event-type bridge in `PushToHubRelay` (PR #25) + `RemoteEventDispatcher` HostedService bridging `RemotePushEvent` envelope → typed events (PR #28, v2.5.0) + **PascalCase fix**: dropped `[JsonSourceGenerationOptions(PropertyNamingPolicy=CamelCase)]` because the SDK `Pro.Push.Redis.RedisEventRelay.SerializePayload()` 3-arg `JsonSerializer.Serialize(obj, runtimeType, options)` overload IGNORES the attribute-level naming policy (PR #29, v2.5.1). Final harness run validated on `v2.5.1` rev 25: Total Forwarded=10 (1 leader pod), Total SkippedNotLeader=30 (3 followers × 10), Receives=10 × 5 clients ✅. Evidence: [`docs/operations/harness-evidence/exactly-once-v2.5.1-2026-05-24.md`](docs/operations/harness-evidence/exactly-once-v2.5.1-2026-05-24.md). Reusable harness shipped in [`tests/Verbara.Platform.E2E.Harness/`](tests/Verbara.Platform.E2E.Harness/) (walking skeleton; 7 future scenarios deferred). **R5.5 Phase B-LK now UNBLOCKED** (was gated on Test 5 PASS).
> - **R5.5 Phase B-LK baseline measured 2026-05-24 (v2.5.1) + closure rerun 2026-05-25 (v2.5.2 post-PR #32 fix):** Initial baseline against v2.5.1 surfaced 4 pod-restart cascade on platform-api during VU=1500 burst (4 livenessProbe-timeout kills). Deep root-cause analysis identified the REAL cause: `/health` (liveness) and `/health/ready` (readiness) ran the same check suite (all `AddCheck<T>` tagged `["ready"]`), including `PostgresHealthCheck`'s live SQL ping. Under burst, the SQL ping queued behind real traffic, exceeded chart's default `livenessProbe.timeoutSeconds: 1`, K8s correctly enforced contract → restart cascade. **NOT a timeout-tuning issue — a K8s contract violation.** Fix shipped via ADR-0025 (PR #32) + sweep harness resilience (PR #33) + v2.5.2 release: `/health` is now `Predicate=_=>false` no-op (responds <1ms regardless of dependency state); `/health/ready` keeps full check suite. Chart defensive `timeoutSeconds:1→3 + failureThreshold:3→5` on all 3 probes. Validation rerun on v2.5.2: **0 pod restarts (vs prior 4), 0 Unauthorized (vs prior 1000), p99 3.7s (vs prior 12.4s), 43,184 OK (vs prior 34,755)**. Evidence: [`docs/operations/r55-blk-evidence/2026-05-25-v252-pr32-validation/`](docs/operations/r55-blk-evidence/2026-05-25-v252-pr32-validation/). JTI investigation in [`docs/research/2026-05-24-jti-investigation-presence-vu1500.md`](docs/research/2026-05-24-jti-investigation-presence-vu1500.md) refuted the original "JTI cache turnover" hypothesis — the 1k Unauthorized were secondary cascade after pod-restart cleared the per-pod validation-key cache. Latent sync-over-async path identified in `JwtTokenService.GetCachedValidationKeys` (Tier-1 hardening filed as low-priority defense-in-depth, no longer blocking R5.5).
> - **ADR-0026 Phase A — ✅ SHIPPED 2026-05-28 (`main` only, no release tag yet):** Membership executive routing groundwork — wizard fix (`createdQueueId` flowthrough + agent-step "create new user" only) + channel-aware `queue_memberships.allowed_channels TEXT[]` model + REST `QueueMembersEndpoints` (`AllowedChannels` POST + PATCH PUT-like with `clearAllowedChannels` flag + voice-include-diff Asterisk sync gate) + agent-centric `GET /admin/agents/{id}/queue-memberships` + Web editor at [`/admin/agents/{id}/queues`](../Verbara.Platform.Web/src/admin/agents/agent-queues.tsx) (channel chip multi-select + "Voice → Asterisk" / "Digital only" sync badge + scoped `membership-card-{queueId}` testid) + 13 Api.Tests locking the contract (961 / 961 suite green). Phase A.6.7 (Day 2 living-docs journey "Restringir agente a WebChat") shipped alongside, manual rendered at [`docs/manuales/auto/v2.5.5/es-419/smb-owner/02-agent-channel-routing.md`](docs/manuales/auto/v2.5.5/es-419/smb-owner/02-agent-channel-routing.md). 6 commits across Platform + Web (Platform [`0ddb511d`](https://github.com/verbara/Verbara.Platform/commit/0ddb511d) + [`53c0ac61`](https://github.com/verbara/Verbara.Platform/commit/53c0ac61) + [`442e3ad9`](https://github.com/verbara/Verbara.Platform/commit/442e3ad9); Web [`a283666`](https://github.com/verbara/Verbara.Platform.Web/commit/a283666) + [`1d4dce2`](https://github.com/verbara/Verbara.Platform.Web/commit/1d4dce2) + [`899594d`](https://github.com/verbara/Verbara.Platform.Web/commit/899594d)). Local images `ghcr.io/verbara/platform/{api,web}:local-phase-a` on lab.
> - **ADR-0026 Phase B — ✅ SHIPPED 2026-05-29 (`main` only, no release tag yet):** Digital-routing executive gate closed in one day after the original 2026-06-28 calendar gate was retired (the freeze rule was derogated by the 2026-05-25 pivot — no production to protect). **SDK Pro v2.6.0-pro** ([Pro `913ec98`](https://github.com/verbara/Verbara.Sdk.Pro/commit/913ec98)) ships `IRealtimeSyncService.AddQueueMemberAsync(allowedChannels)` with the voice-gate ENCAPSULATED inside `RealtimeSyncEngine` (short-circuit to `RemoveQueueMemberAsync` when populated list omits `"voice"`, idempotent upsert otherwise) + 5 new tests + 24 packages pushed to `/media/Data/Source/Verbara/local-nuget-feed/`. **Platform** ([`b731c1fc`](https://github.com/verbara/Verbara.Platform/commit/b731c1fc) + [`a6220698`](https://github.com/verbara/Verbara.Platform/commit/a6220698)) consumes it: 21 Pro pins bumped in `Directory.Packages.props`, 4 call sites simplified to single-line passthroughs (AdminEndpoints + QueueMembersEndpoints), new [`IRoutingEligibilityService`](src/Verbara.Platform.Routing.Inbound/Services/IRoutingEligibilityService.cs) + [`MembershipAwareRoutingEligibilityService`](src/Verbara.Platform.Routing.Inbound/Services/MembershipAwareRoutingEligibilityService.cs) (intersects presence ∩ memberships, sorts ASC by penalty) + [`RoundRobinAgentSelector`](src/Verbara.Platform.Routing.Inbound/RoundRobinAgentSelector.cs) refactored to penalty-grouped round-robin + sticky bypass when preferred agent has membership in any active queue + is reachable, [`RealtimeReconciliationService`](src/Verbara.Platform.Api/Services/RealtimeReconciliationService.cs) forward-only convergent BackgroundService (cadence = `RealtimeOptions.ReconcilerIntervalSeconds`, default 60s; meter `Verbara.Platform.Realtime.Reconciliation` exposed via OpenTelemetry), [`scripts/infer-memberships-from-skills.sh`](scripts/infer-memberships-from-skills.sh) idempotent legacy backfill (`ON CONFLICT DO NOTHING`, `--dry-run` supported, uses real `agents.skills JSONB` ∩ `queue_configs.required_skills JSONB` schema). **13 new tests** (Api.Tests 1013→1017 reconciliation + Routing.Inbound.Tests 32→41 gate). **Design deviation from original plan**: forward-only convergent reconciler INSTEAD OF `IRealtimeVerifier.VerifyAllAsync`-based diff — the verifier needs `IAmiConnection` per tenant, impractical from a BackgroundService. The shipped design re-issues `AddQueueMemberAsync` for every non-excluded membership and trusts the SDK voice-gate to do the right thing per row. Orphan rows in Asterisk are NOT detected here (handled by the migration script for legacy upgrades + `CleanupTenantAsync` during tenant deletion). SMB manuals [`docs/manuales/smb/03-setup-inicial.md`](docs/manuales/smb/03-setup-inicial.md) §4b + [`docs/manuales/smb/04-canal-webchat.md`](docs/manuales/smb/04-canal-webchat.md) §3.1 refreshed with routing-ejecutivo semantics + Platform Admin advisory ("impersoná un Customer" — also closes ADR-0027 C.2 deferred). ADR closure + sub-phase commit table at [`docs/decisions/0026-queue-membership-executive-routing.md`](docs/decisions/0026-queue-membership-executive-routing.md) §Implementation status. Plan moved `docs/plans/active/` → [`docs/plans/completed/2026-05-29-membership-executive-gate.md`](docs/plans/completed/2026-05-29-membership-executive-gate.md). Local API image `ghcr.io/verbara/platform/api:local-phase-b` on lab (Web unchanged from Phase A). **Net result: `queue_memberships` is now the executive source of truth for routing across ALL channels (voice + digital). SMB Docker product polish track is ZERO routing tech debt.**
> - **Current baseline (2026-05-30):** Platform **v2.6.0 on `main`** (local tag `v2.6.0`, NOT yet pushed/released) — ADR-0026 Phase A + Phase B + **ADR-0028 setup creates a mandatory Customer tenant** (`POST /api/setup` now provisions `platform` + first `Customer` + both admins; breaking contract change, all in-repo callers migrated). Prior chain: v2.5.2 K8s liveness/readiness contract fix → v2.5.3 JWT Tier-1 hardening TTL 60s→5min + stale-cache fallback → v2.5.4 OTel meter `verbara.platform.jwt` → Phase A `b731c1fc` → Phase B `a6220698` → setup-multitenant `73f8ce96`. Lab on Helm rev 29 image `sha256:05ccb4fb` for K8s soak workloads. · SDK **2.2.1** · **Pro 2.6.0-pro** (bumped from 2.5.1-pro on 2026-05-29 for Phase B `IRealtimeSyncService.AddQueueMemberAsync(allowedChannels)` signature change). **Release of v2.6.0 deferred per the 2026-05-25 pivot (push gated on first paying customer); before release: re-point the `v2.6.0` tag to `main` HEAD, publish Pro 2.6.0-pro to GitHub Packages, then push tag through `release.yml`.** **R5.5 "Production Validation con datos reales" train SHIPPED 2026-05-26** — Phase F closed with Production Readiness Review at [`docs/operations/production-readiness-review.md`](docs/operations/production-readiness-review.md). 2 of 3 planned datasets measured: Docker D-L 24h PASS 2026-04-30 + K8s local Talos lab B-LK/C-LK/D-LK closed on v2.5.4. Cloud envelope (`0C`/`B-C`/`D-C`/`E-C`) **deferred indefinitely** per 2026-05-25 strategic pivot (commit `204aa7c9` — no cloud until first paying customer). R5.5 spec + execution plan moved `docs/plans/active/` → `docs/plans/completed/`. Sign-off: SMB Docker single-host = fit-for-first-paying-customer; Enterprise K8s on-prem = fit-for-design-partner / pilot; cloud NOT claimed production-ready. Permanent architectural artifacts shipped across the R5.5 train: (1) [ADR-0025](docs/decisions/0025-health-liveness-readiness-contract.md); (2) [`src/Verbara.Platform.Api/Program.cs:1257`](src/Verbara.Platform.Api/Program.cs) health endpoint split; (3) chart probe defensive tuning in [`infra/k8s/helm/platform/templates/platform-api-deployment.yaml:191`](infra/k8s/helm/platform/templates/platform-api-deployment.yaml); (4) [`scripts/scenario-sweep.sh`](scripts/scenario-sweep.sh) preserve-step pattern + token refresh resilience; (5) 3 helper scripts for B-LK validation runs: [`scripts/rerun-blk-validation.sh`](scripts/rerun-blk-validation.sh) + [`scripts/post-blk-finalize.sh`](scripts/post-blk-finalize.sh) + [`scripts/aggregate-blk-results.sh`](scripts/aggregate-blk-results.sh). Plus from prior Phase A.5 session: shared `PlatformPushJsonContext`, `RemoteEventDispatcher`, `AdminRealtimeAuditEndpoint`, `tests/Verbara.Platform.E2E.Harness/` walking skeleton.
> - **Typification train (ADR-0029) — current baseline Platform `v2.12.0` (tag), pinned Pro `2.8.0-pro`, SDK `2.2.1`; `Directory.Build.props` PackageVersion = 2.12.0; Api.Tests ≈ 1357; migrations through `003`.** New first-class `Verbara.Platform.Typification` module — clean-break of the flat `Disposition`, all migrations squashed to `001_Baseline`. Phases shipped P0 · P1 · P2a:
>   - **P0 ✅ 2026-06-07 (v2.10.0, Pro 2.7.5-pro):** cascading + conditional schema-driven disposition forms; `LicenseFeature.AdvancedTypification`; runtime `GET/POST /conversations/{id}/typification-{form,typify}` + admin `/admin/typification/*`.
>   - **P1 ✅ 2026-06-08 (v2.11.0):** shared taxonomy capture — IVR/bot/routing reason → `Conversation.Metadata.reasonPath` → prefill resolver pre-selects the wrap-up cascade + fills fields (4 writers: flow-vars propagation, `collect_reason` flow node, `ReasonHintMiddleware`→`RouteResult.Metadata`, voice `VERBARA_REASON` channel var). New `ReasonHint` domain + `/admin/reason-hints`. **Surfaced + fixed the dormant flow engine** (`AddPlatformFlows` was never wired) with a disabled-by-default `ILlmProvider` stub.
>   - **P2a ✅ 2026-06-10 (v2.12.0, Pro 2.8.0-pro):** AI auto-disposition — **new open AOT project `Verbara.Platform.Llm`** (the first real LLM integration; `ILlmProvider` moved here from Flows to break the Flows→Typification cycle + `OpenAiCompatibleLlmProvider` covering OpenAI/Azure/local; also activates the `ai_classify`/`ai_generate` flow nodes) + `ITypificationAiClassifier` + gated `POST /conversations/{id}/typification-suggestion` (`LicenseFeature.TypificationAi`). Bundled a deterministic typification-resolution flaky-fix (`CreatedAt` tiebreak, migration 003) + a cosign-sign retry loop in `release.yml`.
>   - **Next (designed, not started):** **P2b** (AutoApply + entity prefill + Pro voice enrichment — must supply the real `ILlmProvider` replacing the stub) · **P3** (data-dips) · **P4** (analytics + builder).

> **This repo is the authoritative workstream for Platform + Platform.Web.** Plans, specs, ADRs, and research that touch either the API **or** the React frontend are authored under this repo's `docs/` tree. `Verbara.Platform.Web` remains a separate git repo for frontend source, but its own `docs/` is secondary — open new plans here. Decision recorded 2026-04-19 (feedback memory `feedback_platform_web_consolidation.md`).

> **Do not append completed-work narrative to this file.** Milestone/sprint/plan write-ups belong in `~/.claude/projects/-media-Data-Source-Verbara-Verbara-Platform/memory/` (indexed by `MEMORY.md`). Only evergreen context — what the codebase IS, not what it WAS — lives here.

## Project Overview

Verbara.Platform is the API host and composition root for the omnichannel contact center. .NET 10. Consumes SDK (MIT) and Pro packages via NuGet — versions pinned in `Directory.Packages.props`. The SDK + Pro libraries AND the Api host are all AOT-compatible (`IsAotCompatible=true`); Phase D (2026-05-20) removed the last blocker (Dapper) cross-repo, so `Verbara.Platform.Api` now publishes Native AOT — see the AOT shipping note above and [ADR-0022](docs/decisions/0022-platform-api-aot-shipping-path.md).

**`/api/v1/` (URL-segment versioning), 70 endpoint groups (14 with feature gates).** Current version in `Directory.Build.props`; package list under `src/`.

**Package layers** (purpose-grouped; one DI extension per package):

- **Core domain:** `Core` (abstractions, IClock, GDPR, Webhooks, Plans, Feature Gates), `Identity` (Users, RBAC, API keys, OIDC SSO), `Conversations` (14-state lifecycle, Contacts, Cases, Tags), `Queues` (SLA, per-channel agent capacity, Teams), `Switchboard` (assign/offer/accept/reject/transfer), `Routing.Inbound` (channel→queue, last-agent, priority, overflow, business hours)
- **Channels:** `Channels.Core` (registry, inbound pipeline, delivery status) + 11 connectors: `WhatsApp` (Meta + HMAC + 24h window), `Sms` (provider-agnostic, segment calc, Twilio provider), `WebChat` (session + WebSocket), `Messenger`, `Instagram`, `Telegram`, `Email`, `Video`, `Twitter`, `Rcs`
- **AI / Workflow:** `Flows` (DAG, 11 node types, LLM abstraction), `Bot` (virtual agent + analytics), `KnowledgeBase`, `Automation` (triggers + conditions + actions), `Surveys`
- **Cross-cutting:** `Audit`, `Media` (FileSystem + S3 backends), `Billing` (metering, quotas, rate cards, invoices, dunning)
- **Microservices:** `Renderer` (`:5010` — QuestPDF + ScottPlot PDF/CSV), `Mail` (`:5020` — MailKit SMTP + MS Graph + OAuth PKCE)
- **Storage:** `Storage.InMemory` (dev/test default), `Storage.Postgres` (raw Npgsql via `Verbara.Sdk.Data.Npgsql` facade — NOT Dapper, RBAC seeder)
- **Host:** `Api` (this composition root)

## Build & Test

```sh
dotnet build Verbara.Platform.slnx
dotnet test Verbara.Platform.slnx               # all tests
dotnet test Verbara.Platform.slnx -v q          # quiet
dotnet test tests/Verbara.Platform.Api.Tests/   # single project
```

## Running the Platform

Platform.Api is the composition root and executable host.

```sh
cd src/Verbara.Platform.Api
dotnet run
```

## Architecture

### Platform.Api -- Composition Root

`Program.cs` registers all platform packages + Pro packages (Dialer, EventStore, Analytics, CallAnalytics, AgentAssist, Realtime, Cluster, MultiTenant, Licensing), configures dual-scheme auth, RBAC, rate limiting, CORS, health checks, and maps 70 endpoint groups.

Storage.InMemory provides drop-in defaults. PostgreSQL storage activates via connection strings (`Dialer`, `Analytics`, `Realtime`, or fallback `Postgres`).

### Inbound Message Flow

```
Webhook (WhatsApp/SMS/WebChat/Email/Telegram/etc.)
  -> IWebhookHandler.HandleAsync -> WebhookResult: NewMessage | StatusUpdate | Ignored
  -> NewMessage path:
      -> IInboundMessagePipeline.ProcessAsync
          -> DeduplicateStep / ContactResolutionStep / ConversationResolutionStep / MessagePersistenceStep
      -> IInboundRouter.RouteAsync (optional)
          -> ChannelQueueMapping / LastAgent / PriorityEscalation / Overflow / BusinessHours
      -> IConversationSwitchboard.AssignToQueueAsync / OfferToAgentAsync
  -> StatusUpdate path:
      -> DeliveryStatusHandler -> IMessageStore.UpdateDeliveryStatusAsync
```

### Middleware Pipeline

```
ErrorHandling -> CORS -> RateLimiter -> TenantResolution -> Authentication -> Authorization
```

### Auth & RBAC

- **Dual-scheme:** JWT (RS256) + API key (`X-Api-Key`)
- **JWT flow:** Email/Password login -> access + refresh token. MFA via TOTP (per-tenant policy).
- **OIDC SSO:** per-tenant, Authorization Code + PKCE + nonce
- **API Keys:** M2M, scoped, SHA-256 hashed (types: `Tenant`, `Management`)
- **Sessions:** idle + absolute timeout, revocation
- **Lockout + password policies:** per-tenant
- **RBAC:** 64 permissions (`domain:resource:action`), 8 role templates (Agent, Supervisor, Quality Analyst, Manager, Admin, System Admin, API, Platform Admin), custom roles per-tenant, cascading via `PermissionResolver` + `PermissionAuthorizationHandler`
- **Policies:** `AdminOnly`, `SupervisorPlus`, `Authenticated`, `PlatformAdminOnly`, `PartnerAdminOnly`

### Endpoint Inventory

All endpoints in `src/Verbara.Platform.Api/Endpoints/` (file-per-group). Routes versioned under `/api/v1/` (Asp.Versioning.Http, URL-segment). Legacy `/api/` redirects for backward compat.

**Categories:** Auth (incl. OIDC, RBAC, AuthAdmin) · Omnichannel (Webhook, Conversation, ChannelConfig, Contact, SSE, WebChat) · Agent (Agent, Supervisor, Skill, UsersMe) · Admin (Admin, Audit, ScheduledReport, TenantSettings) · Management (Tenant/Settings/System/Cluster/ApiKey/Billing/Impersonation/Webhook + Setup) · GDPR · Outbound Webhooks (Subscription, EventType) · Dialer (Campaign, CallAttempt, DncList, CallerIdPool, HolidayCalendar, Settings, Trunk, OutboundRoute) · Analytics (incl. Live + QueueMetrics) · AI/Bot (Bot, KnowledgeBase, AgentAssist, Flow) · Media (incl. Recording) · Realtime + Cluster · Partner (Customer/Billing/Revenue/Settings) · Branding (public) · Notifications · Onboarding · Misc (CannedResponse, Case, Disposition, Survey).

`ls src/Verbara.Platform.Api/Endpoints/` for the authoritative file list.

### Pro Package Integration

Platform.Api consumes 16 Pro NuGet packages:

```
Pro.Dialer + Pro.Dialer.Storage.Postgres            -- Outbound campaigns
Pro.EventStore + Pro.EventStore.Postgres            -- Event sourcing, CDR
Pro.Analytics + Pro.Analytics.Storage.Postgres      -- Real-time metrics
Pro.CallAnalytics + Pro.CallAnalytics.Storage.Postgres -- Post-call AI
Pro.AgentAssist + Pro.AgentAssist.Storage.Postgres  -- Live agent assist
Pro.Realtime + Pro.Realtime.Storage.Postgres        -- Asterisk PBX Realtime DB
Pro.Cluster + Pro.Cluster.Storage.Postgres          -- Multi-server clustering
Pro.MultiTenant / Pro.Routing / Pro.Licensing       -- Tenant isolation, skill routing, license enforcement
```

## Docker Deployment

```sh
docker compose -f docker/docker-compose.full.yml up          # Full stack (Asterisk 22 PBX + API + Web + Postgres 18 + Redis 8 + MinIO)
docker compose -f docker/docker-compose.production.yml up    # Production (no dev seeds, external DB)
docker compose -f docker/demo/docker-compose.demo.yml up     # Demo (pre-seeded, simulated PSTN)
docker compose up                                             # Dev (root-level, API only)
```

**Demo invariant:** `docs/specs/demo-environment.md` MUST be updated whenever any file under `docker/demo/` changes.

## DI Registration (Composition Root)

Order in `Program.cs`:

1. **SDK:** `AddVerbara(Configuration)` (AMI + ARI), `AddVerbaraSessions()`
2. **Platform core:** one `AddPlatform*()` per package (Core, Conversations, Channels, InboundRouting, Switchboard, Bot, Audit, Media, KnowledgeBase, Surveys, Billing) + cross-cutting: `AddPlatformRateLimiting` (per-tenant tiers), `AddPlatformScheduledReports` (NCrontab + Renderer + Mail), `AddPlatformApiVersioning` (`/api/v1`)
3. **Storage:** `AddInMemoryStorage()` by default; `AddPostgresStorage(connString)` when configured
4. **Pro (conditional on connection strings):** Dialer (`UsePostgresDialerStorage` + `AddProDialer`), Realtime (`AddVerbaraRealtime` + `UsePostgresRealtimeStorage`), EventStore (`UsePostgresEventStore` + `AddVerbaraEventStore`), Analytics, CallAnalytics, AgentAssist, Cluster (`AddVerbaraCluster` + `UsePostgresClusterTransport`), MultiTenant, Licensing
5. **Auth:** `AddDynamicAuth(jwtTokenService)` + singleton `PermissionResolver` + `PermissionAuthorizationHandler` + `PermissionPolicyProvider`

Exact lines live in `src/Verbara.Platform.Api/Program.cs` — this section only reflects the ordering rules.

## Code Conventions

- **No `Co-Authored-By` in commits.** Conventional Commits (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`).
- **AOT:** no reflection. `[JsonSerializable]`, `[LoggerMessage]`, static dispatch. `EnableRequestDelegateGenerator=true`.
- Async-first with `CancellationToken`.
- Private fields: `_camelCase`. File-scoped namespaces.
- Test naming: `Method_ShouldExpected_WhenCondition`.
- Test stack: xunit 2.9.3, FluentAssertions 7.1.0, NSubstitute 5.3.0.
- `TreatWarningsAsErrors` ON, `WarningLevel 9999`. Central package management in `Directory.Packages.props`.
- Key NuGet: Npgsql 10.0.2 + `Verbara.Sdk.Data.Npgsql` (data access — NO Dapper, banned), BCrypt.Net-Next 4.0.3, System.IdentityModel.Tokens.Jwt 8.7.0, Asp.Versioning.Http, NCrontab. QuestPDF + ScottPlot in Renderer only. MailKit + Microsoft.Graph + Azure.Identity in Mail only.

### Critical Gotchas

- **PostgreSQL 18 + Redis 8:** all compose files on `postgres:18-alpine` / `redis:8-alpine`.
- **JWT claims:** `MapInboundClaims = false` + `RoleClaimType = "role"` + `NameClaimType = "sub"`. .NET 10 maps claims by default; without this, `FindFirst("tid")` fails and `RequireRole("Admin")` breaks.
- **Tenant resolution:** `OnTokenValidated` only sets `TenantId` when middleware hasn't already — `X-Tenant-Id` header / subdomain wins over JWT `tid` for cross-tenant admin access.
- **Npgsql via `Verbara.Sdk.Data.Npgsql` (Dapper banned — ADR-0022):** row types are class-based `{ get; init; }` + a hand-written `static Map(NpgsqlDataReader)` (name-based getters); params bound explicitly (no anon objects). **Every nullable param that can be `DBNull.Value` MUST set an explicit `NpgsqlDbType`** or Postgres throws `42P08` (jsonb string params with a `::jsonb` cast excepted). `COUNT(*)` → `ExecuteScalarAsync<long?>(...) ?? 0L`. `QuerySingleOrDefaultAsync` throws on >1 row (use `QueryFirstOrDefaultAsync` for first-of-many). Npgsql 10 returns `DateTime` for `timestamptz` (the `Npgsql.EnableLegacyTimestampBehavior` host config option is preserved via `RuntimeHostConfigurationOption`).
- **DTO hardening (Plan 29A pattern):** never return anonymous `new {}`. Use typed sealed records registered in `ApiJsonContext`. DTO field is `id` (not `teamId`/`userId`/etc.) so frontend hooks work.
- **E2E conventions:** locale-proof selectors (`data-*` over `toContainText`); `ConfirmDeleteDialog` (3s countdown) for destructive actions; shadcn `Select` uses `role=option` not `selectOption()`; always `data-table-search.fill(id)` before clicking a freshly created row.

## Documentation Layout (all git-tracked, private repo)

| Folder | Purpose | Lifecycle |
|--------|---------|-----------|
| `docs/specs/` | Technical designs (input to implementation) | Add on new feature, rarely edited after |
| `docs/specs/archived/` | Superseded / draft specs kept for history | Append-only |
| `docs/decisions/` | ADRs — architecture decision records (why, not how) | Append-only; never delete |
| `docs/plans/active/` | Execution plans currently in progress | Moves to `completed/` on ship |
| `docs/plans/completed/` | Shipped plans, preserved as historical record | Append-only |
| `docs/plans/archived/` | Skeletons / superseded / abandoned plans | Append-only |
| `docs/research/` | Exploratory findings, market analysis, discovery | Freeform |
| `docs/research/archived/` | Older research kept for context | Append-only |
| `docs/manuales/smb/` | **Customer-facing manuales (español)** — step-by-step deployment guide for SMB on-premise; the source of truth for the operator that installs Verbara at a customer site. 12 archivos cubriendo install (01) → arranque (02) → setup wizard (03) → 3 canales V1 (04 WebChat, 05 Email, 06 Voz/SIP) → validación E2E (07) → troubleshooting SIP (08) + general (99) + checklist firmable + capacity reference. Cualquier cambio en `docker/docker-compose.reference-smb.yml`, `scripts/quickstart-smb.sh`, o el flujo del setup wizard DEBE reflejarse acá en el mismo commit. | Edit on relevant feature change |
| `docs/manuales/k8s/` (Fase 2 — pending) | Customer-facing K8s on-prem manuales — spejean los SMB para deploy K8s | Future |

After `ExitPlanMode` approval, copy the system-path plan file (`~/.claude/plans/*.md`) into `docs/plans/active/` with a date-prefixed meaningful name — the repo is authoritative. When the plan ships, `git mv` it to `docs/plans/completed/`.

ADR numbering is sequential (`0001`, `0002`, …). Once `Accepted`, ADRs are append-only — supersede with a new ADR that references the predecessor.

## Plan Execution

**Always use Subagent-Driven Development** with risk-weighted batching (FCM pattern):
- Phase A -- Foundation (scaffolding, models): batch
- Phase B -- Critical components (serializers, calculators): individual focused subagents
- Phase C -- Integration (DI, storage, wiring): batch

Spec + Plan must be approved before code. Update plan file as steps complete.

## Milestone History

Evergreen roadmap and completed-milestone narrative live in `~/.claude/projects/-media-Data-Source-Verbara-Asterisk-Platform/memory/MEMORY.md` and its topic files (`project_*.md`, `feedback_*.md`, `research_*.md`, `reference_*.md`). Do not re-inline them here.

Latest milestones (pointers only):
- **v1.5.0 "Production Ready"** (2026-04-09) — see `project_v150_production_ready.md`
- **v1.5.0 Web Sync** (2026-04-10) — Plan 33, see `project_web_sync_analysis.md`
- **v1.6.0 "Production Polish"** (2026-04-11) — Subs A/C/D/E complete, Sub B deferred, see `project_v160_production_polish.md`
- **Plan 36: Last 4 E2E fails closed** (2026-04-12) — see `project_plan36_bots_queues_wallboard.md`
- **Next:** push v1.6.0 → SSE tech debt (30 min) → v1.7.0 "Reseller Enablement + Security Expansion" (Axis B). Full roadmap in MEMORY.md.
