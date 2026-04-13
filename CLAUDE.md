# CLAUDE.md

> **Do not append completed-work narrative to this file.** Milestone/sprint/plan write-ups belong in `~/.claude/projects/-media-Data-Source-IPcom-Asterisk-Platform/memory/` (indexed by `MEMORY.md`). Only evergreen context — what the codebase IS, not what it WAS — lives here.

## Project Overview

Asterisk.Platform is the API host and composition root for the omnichannel contact center. .NET 10 Native AOT. Consumes MIT SDK packages via NuGet (v1.8.0) and Pro packages (v1.2.0-pro).

**30 packages, 1,636 tests, 0 warnings, NativeAOT (`IsAotCompatible=true`), 59 endpoint groups (14 with feature gates), version 1.7.0.**

| Package | Purpose | Tests |
|---------|---------|-------|
| Platform.Core | Abstractions, value objects, IClock, GDPR, Webhooks, Plans, Feature Gates, DI | 96 |
| Platform.Identity | Users, RBAC, API keys, service accounts, OIDC SSO, DI | 26 |
| Platform.Conversations | Conversation lifecycle (14 states), Contact CRM-lite, Cases, Tags, DI | 73 |
| Platform.Queues | Queue config, SLA, Agent with per-channel capacity, Teams, DI | 44 |
| Platform.Channels.Core | Channel registry, inbound pipeline, delivery status, tenant config, DI | 38 |
| Platform.Channels.WhatsApp | Meta Business API connector, HMAC webhook, 24h session window, DI | 29 |
| Platform.Channels.Sms | Provider-agnostic SMS, segment calculator, Twilio provider, DI | 32 |
| Platform.Channels.WebChat | WebChat connector, session manager, transport abstraction, DI | 25 |
| Platform.Channels.Messenger | Messenger connector, DI | 22 |
| Platform.Channels.Instagram | Instagram connector, DI | 16 |
| Platform.Channels.Telegram | Telegram connector, DI | 24 |
| Platform.Channels.Email | Email connector, DI | 41 |
| Platform.Channels.Video | Video connector, DI | 26 |
| Platform.Channels.Twitter | Twitter connector, DI | 27 |
| Platform.Channels.Rcs | RCS connector, DI | 33 |
| Platform.Routing.Inbound | Channel mapping, last-agent, priority, overflow, business hours, DI | 32 |
| Platform.Switchboard | Conversation ownership -- assign, offer, accept, reject, transfer, DI | 38 |
| Platform.KnowledgeBase | Knowledge search abstraction, DI | 19 |
| Platform.Flows | DAG workflow engine -- 11 node types, persistent exec, LLM abstraction, DI | 52 |
| Platform.Bot | Virtual agent orchestration, flow-driven turn management, analytics, DI | 30 |
| Platform.Automation | Event triggers, condition evaluator, action executor, DI | 45 |
| Platform.Surveys | Post-conversation surveys, response collection, DI | 30 |
| Platform.Audit | Audit trail -- event logging, query, retention, DI | 41 |
| Platform.Media | Media storage abstraction, FileSystem + S3 backends, recording options, DI | 10 |
| Platform.Billing | Metering, quota enforcement, rate cards, invoices, dunning, DI | 46 |
| Platform.Renderer | Stateless PDF/CSV microservice (`:5010`) -- QuestPDF + ScottPlot | 0 |
| Platform.Mail | Email + MS 365 Graph microservice (`:5020`) -- MailKit SMTP, Graph, OAuth PKCE | 0 |
| Platform.Storage.InMemory | In-memory store implementations -- dev/test, DI | 125 |
| Platform.Storage.Postgres | PostgreSQL stores, RBAC seeder, Npgsql + Dapper | 6 |
| Platform.Api | HTTP host -- 59 endpoint groups, auth, middleware, SSE, NativeAOT | 574 |

## Build & Test

```sh
dotnet build Asterisk.Platform.slnx
dotnet test Asterisk.Platform.slnx               # all tests
dotnet test Asterisk.Platform.slnx -v q          # quiet
dotnet test tests/Asterisk.Platform.Api.Tests/   # single project
```

## Running the Platform

Platform.Api is the composition root and executable host.

```sh
cd src/Asterisk.Platform.Api
dotnet run
```

## Architecture

### Platform.Api -- Composition Root

`Program.cs` registers all platform packages + Pro packages (Dialer, EventStore, Analytics, CallAnalytics, AgentAssist, Realtime, Cluster, MultiTenant, Licensing), configures dual-scheme auth, RBAC, rate limiting, CORS, health checks, and maps 59 endpoint groups.

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

All endpoints in `src/Asterisk.Platform.Api/Endpoints/`. Routes versioned under `/api/v1/` (Asp.Versioning.Http, URL-segment). Legacy `/api/` redirects for backward compat.

| Category | Endpoint Files |
|----------|---------------|
| Auth | AuthEndpoints, AuthAdminEndpoints, OidcEndpoints, RbacEndpoints |
| Omnichannel | WebhookEndpoints, ConversationEndpoints, ChannelConfigEndpoints, ContactEndpoints, SseEndpoints |
| Agent | AgentEndpoints, SupervisorEndpoints, SkillEndpoints, UsersMeEndpoint |
| Admin | AdminEndpoints, AuditEndpoints, ScheduledReportEndpoints, TenantSettingsEndpoints |
| Management | ManagementTenant/Settings/System/Cluster/ApiKey/Billing/Impersonation/Webhook, SetupEndpoints |
| GDPR | GdprEndpoints |
| Webhooks (outbound) | WebhookSubscriptionEndpoints, WebhookEventTypeEndpoints |
| Dialer | Campaign, CallAttempt, DncList, CallerIdPool, HolidayCalendar, DialerSettings, Trunk, OutboundRoute |
| Analytics | AnalyticsEndpoints, AnalyticsLiveEndpoints, QueueMetricsEndpoints |
| AI/Bot | BotEndpoints, KnowledgeBaseEndpoints, AgentAssistEndpoints, FlowEndpoints |
| Media | MediaEndpoints, RecordingEndpoints |
| Realtime | RealtimeEndpoints, ClusterEndpoints |
| Partner | PartnerCustomer/Billing/Revenue/Settings |
| Branding | BrandingEndpoints (public, no auth) |
| Notifications | NotificationEndpoints |
| Onboarding | OnboardingEndpoints |
| WebChat | WebChatEndpoints (session, WebSocket, REST fallback) |
| Other | CannedResponseEndpoints, CaseEndpoints, DispositionEndpoints, SurveyEndpoints |

### Pro Package Integration

Platform.Api consumes 16 Pro NuGet packages:

```
Pro.Dialer + Pro.Dialer.Storage.Postgres            -- Outbound campaigns
Pro.EventStore + Pro.EventStore.Postgres            -- Event sourcing, CDR
Pro.Analytics + Pro.Analytics.Storage.Postgres      -- Real-time metrics
Pro.CallAnalytics + Pro.CallAnalytics.Storage.Postgres -- Post-call AI
Pro.AgentAssist + Pro.AgentAssist.Storage.Postgres  -- Live agent assist
Pro.Realtime + Pro.Realtime.Storage.Postgres        -- Asterisk Realtime DB
Pro.Cluster + Pro.Cluster.Storage.Postgres          -- Multi-server clustering
Pro.MultiTenant / Pro.Routing / Pro.Licensing       -- Tenant isolation, skill routing, license enforcement
```

## Docker Deployment

```sh
docker compose -f docker/docker-compose.full.yml up          # Full stack (Asterisk 22 + API + Web + Postgres 18 + Redis 8 + MinIO)
docker compose -f docker/docker-compose.production.yml up    # Production (no dev seeds, external DB)
docker compose -f docker/demo/docker-compose.demo.yml up     # Demo (pre-seeded, simulated PSTN)
docker compose up                                             # Dev (root-level, API only)
```

**Demo invariant:** `docs/demo-environment.md` MUST be updated whenever any file under `docker/demo/` changes.

## DI Registration (Composition Root)

```csharp
// ── Core Platform ──
builder.Services.AddAsterisk(builder.Configuration);     // AMI + ARI
builder.Services.AddAsteriskSessions();
builder.Services.AddPlatformCore();
builder.Services.AddPlatformConversations();
builder.Services.AddPlatformChannels();
builder.Services.AddInboundRouting();
builder.Services.AddSwitchboard();
builder.Services.AddPlatformBot();
builder.Services.AddPlatformAudit();
builder.Services.AddPlatformMedia();
builder.Services.AddPlatformKnowledgeBase();
builder.Services.AddPlatformSurveys();
builder.Services.AddPlatformBilling();
builder.Services.AddPlatformRateLimiting();              // per-tenant tiers (v1.3.1)
builder.Services.AddPlatformScheduledReports(o => { ... }); // NCrontab + QuestPDF + MailKit
builder.Services.AddPlatformApiVersioning();             // /api/v1 (v1.3.1)

// ── Storage ──
builder.Services.AddInMemoryStorage();                   // default
// or:
builder.Services.AddPostgresStorage(connectionString);

// ── Pro (conditional on connection strings) ──
builder.Services.UsePostgresDialerStorage(connectionString);
builder.Services.AddProDialer(o => { });
builder.Services.AddAsteriskRealtime(o => { o.ReconcilerIntervalSeconds = 60; });
builder.Services.UsePostgresRealtimeStorage(realtimeConn);
builder.Services.UsePostgresEventStore(analyticsConn);
builder.Services.AddAsteriskEventStore();
builder.Services.AddAsteriskAnalytics();
builder.Services.AddProCallAnalytics();
builder.Services.AddProAgentAssistPostgres(analyticsConn);
builder.Services.AddAsteriskCluster(c => { c.InstanceId = Environment.MachineName; });
builder.Services.UsePostgresClusterTransport(clusterConn);
builder.Services.AddAsteriskMultiTenant();
builder.Services.AddProLicensing(o => o.EnforcementMode = EnforcementMode.Enforce);

// ── Auth ──
builder.Services.AddDynamicAuth(jwtTokenService);
builder.Services.AddSingleton<PermissionResolver>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
```

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

## Plan Execution

**Always use Subagent-Driven Development** with risk-weighted batching (FCM pattern):
- Phase A -- Foundation (scaffolding, models): batch
- Phase B -- Critical components (serializers, calculators): individual focused subagents
- Phase C -- Integration (DI, storage, wiring): batch

Spec + Plan must be approved before code. Update plan file as steps complete.

## Milestone History

Evergreen roadmap and completed-milestone narrative live in `~/.claude/projects/-media-Data-Source-IPcom-Asterisk-Platform/memory/MEMORY.md` and its topic files (`project_*.md`, `feedback_*.md`, `research_*.md`, `reference_*.md`). Do not re-inline them here.

Latest milestones (pointers only):
- **v1.5.0 "Production Ready"** (2026-04-09) — see `project_v150_production_ready.md`
- **v1.5.0 Web Sync** (2026-04-10) — Plan 33, see `project_web_sync_analysis.md`
- **v1.6.0 "Production Polish"** (2026-04-11) — Subs A/C/D/E complete, Sub B deferred, see `project_v160_production_polish.md`
- **Plan 36: Last 4 E2E fails closed** (2026-04-12) — see `project_plan36_bots_queues_wallboard.md`
- **Next:** push v1.6.0 → SSE tech debt (30 min) → v1.7.0 "Reseller Enablement + Security Expansion" (Axis B). Full roadmap in MEMORY.md.
