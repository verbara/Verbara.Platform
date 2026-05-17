# CLAUDE.md

Project context for Claude Code working on **Verbara.Platform** — the API host and composition root for an omnichannel contact-center built on .NET 10 Native AOT. Read top-to-bottom for architecture; jump to **Critical Gotchas** for non-obvious pitfalls before editing code.

> **This repo is the authoritative workstream for Platform + Platform.Web.** Plans, specs, ADRs, and research that touch either the API **or** the React frontend are authored under this repo's `docs/` tree. `Verbara.Platform.Web` remains a separate git repo for frontend source, but its own `docs/` is secondary — open new plans here. Decision recorded 2026-04-19 (feedback memory `feedback_platform_web_consolidation.md`).

> **Do not append completed-work narrative to this file.** Milestone/sprint/plan write-ups belong in `~/.claude/projects/-media-Data-Source-Verbara-Verbara-Platform/memory/` (indexed by `MEMORY.md`). Only evergreen context — what the codebase IS, not what it WAS — lives here.

## Project Overview

Verbara.Platform is the API host and composition root for the omnichannel contact center. .NET 10 Native AOT. Consumes SDK (MIT) and Pro packages via NuGet — versions pinned in `Directory.Packages.props`.

**NativeAOT (`IsAotCompatible=true`), `/api/v1/` (URL-segment versioning), 70 endpoint groups (14 with feature gates).** Current version in `Directory.Build.props`; package list under `src/`.

**Package layers** (purpose-grouped; one DI extension per package):

- **Core domain:** `Core` (abstractions, IClock, GDPR, Webhooks, Plans, Feature Gates), `Identity` (Users, RBAC, API keys, OIDC SSO), `Conversations` (14-state lifecycle, Contacts, Cases, Tags), `Queues` (SLA, per-channel agent capacity, Teams), `Switchboard` (assign/offer/accept/reject/transfer), `Routing.Inbound` (channel→queue, last-agent, priority, overflow, business hours)
- **Channels:** `Channels.Core` (registry, inbound pipeline, delivery status) + 11 connectors: `WhatsApp` (Meta + HMAC + 24h window), `Sms` (provider-agnostic, segment calc, Twilio provider), `WebChat` (session + WebSocket), `Messenger`, `Instagram`, `Telegram`, `Email`, `Video`, `Twitter`, `Rcs`
- **AI / Workflow:** `Flows` (DAG, 11 node types, LLM abstraction), `Bot` (virtual agent + analytics), `KnowledgeBase`, `Automation` (triggers + conditions + actions), `Surveys`
- **Cross-cutting:** `Audit`, `Media` (FileSystem + S3 backends), `Billing` (metering, quotas, rate cards, invoices, dunning)
- **Microservices:** `Renderer` (`:5010` — QuestPDF + ScottPlot PDF/CSV), `Mail` (`:5020` — MailKit SMTP + MS Graph + OAuth PKCE)
- **Storage:** `Storage.InMemory` (dev/test default), `Storage.Postgres` (Npgsql + Dapper, RBAC seeder)
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
- Key NuGet: Npgsql 9.0.3, Dapper 2.1.66, BCrypt.Net-Next 4.0.3, System.IdentityModel.Tokens.Jwt 8.7.0, Asp.Versioning.Http, NCrontab. QuestPDF + ScottPlot in Renderer only. MailKit + Microsoft.Graph + Azure.Identity in Mail only.

### Critical Gotchas

- **PostgreSQL 18 + Redis 8:** all compose files on `postgres:18-alpine` / `redis:8-alpine`.
- **JWT claims:** `MapInboundClaims = false` + `RoleClaimType = "role"` + `NameClaimType = "sub"`. .NET 10 maps claims by default; without this, `FindFirst("tid")` fails and `RequireRole("Admin")` breaks.
- **Tenant resolution:** `OnTokenValidated` only sets `TenantId` when middleware hasn't already — `X-Tenant-Id` header / subdomain wins over JWT `tid` for cross-tenant admin access.
- **Npgsql 9 + Dapper:** Postgres row types MUST be class-based with `{ get; init; }`, NOT positional records. Npgsql 9 returns `DateTime` for `timestamptz`; Dapper constructor matching fails with nullable `DateTime?` params. All 43 stores converted.
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
