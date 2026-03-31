# CLAUDE.md

## Project Overview

Asterisk.Platform is the API host and composition root for the omnichannel contact center. .NET 10 Native AOT. Consumes MIT SDK packages via NuGet (v1.5.3) and Pro packages (v1.0.0-pro).

**27 packages, 1068 tests, 0 warnings, AOT-compatible:**

| Package | Purpose | Tests |
|---------|---------|-------|
| Platform.Core | Abstractions, value objects, base interfaces, IClock, DI | 29 |
| Platform.Identity | Users, RBAC, API keys, service accounts, DI | 10 |
| Platform.Conversations | Conversation lifecycle (14 states), Contact CRM-lite, Cases, Tags, DI | 73 |
| Platform.Queues | Queue config, SLA, Agent with per-channel capacity, Teams, DI | 44 |
| Platform.Channels.Core | Channel registry, inbound pipeline, delivery status, tenant config, DI | 38 |
| Platform.Channels.WhatsApp | Meta Business API connector, HMAC webhook, 24h session window, DI | 29 |
| Platform.Channels.Sms | Provider-agnostic SMS connector, segment calculator, DI | 28 |
| Platform.Channels.WebChat | WebChat connector, session manager, transport abstraction, DI | 25 |
| Platform.Channels.Messenger | Messenger connector, DI | 22 |
| Platform.Channels.Instagram | Instagram connector, DI | 16 |
| Platform.Channels.Telegram | Telegram connector, DI | 24 |
| Platform.Channels.Email | Email connector, DI | 41 |
| Platform.Channels.Video | Video connector, DI | 26 |
| Platform.Channels.Twitter | Twitter connector, DI | 27 |
| Platform.Channels.Rcs | RCS connector, DI | 33 |
| Platform.Routing.Inbound | Inbound routing pipeline -- channel mapping, last-agent, priority, overflow, business hours, DI | 32 |
| Platform.Switchboard | Conversation ownership lifecycle -- assign, offer, accept, reject, transfer, DI | 38 |
| Platform.KnowledgeBase | Knowledge search abstraction, DI | 19 |
| Platform.Flows | DAG workflow engine -- 11 node types, persistent execution, template rendering, LLM abstraction, DI | 52 |
| Platform.Bot | Virtual agent orchestration -- IVirtualAgent, BotOrchestrator, flow-driven turn management, analytics, DI | 30 |
| Platform.Automation | Automation rules -- event triggers, condition evaluator, action executor, DI | 45 |
| Platform.Surveys | Post-conversation surveys -- survey store, response collection, DI | 30 |
| Platform.Audit | Audit trail -- event logging, query, retention, DI | 9 |
| Platform.Media | Media storage abstraction, FileSystem + S3 backends, recording options, DI | 10 |
| Platform.Storage.InMemory | In-memory implementations of all stores -- dev/test, DI | 40 |
| Platform.Storage.Postgres | PostgreSQL implementations, RBAC seeder, Npgsql + Dapper | 5 |
| Platform.Api | HTTP host -- 41 endpoint groups, auth, middleware, SSE, OpenAPI | 282 |

## Build & Test

```sh
# Build entire solution
dotnet build Asterisk.Platform.slnx

# Run all tests
dotnet test Asterisk.Platform.slnx

# Quiet output
dotnet test Asterisk.Platform.slnx -v q

# Run a single test project
dotnet test tests/Asterisk.Platform.Api.Tests/
```

## Running the Platform

Platform.Api is the composition root and executable host. It wires all packages plus Pro NuGet packages via DI and exposes the HTTP surface.

```sh
cd src/Asterisk.Platform.Api
dotnet run
```

## Architecture

### Platform.Api -- Composition Root

`Program.cs` registers all platform packages, all Pro packages (Dialer, EventStore, Analytics, CallAnalytics, AgentAssist, Realtime, Cluster, MultiTenant, Licensing), configures auth (JWT + API key dual-scheme), RBAC, rate limiting, CORS, health checks, and maps 39 endpoint groups.

Storage.InMemory provides drop-in in-memory implementations for development. PostgreSQL storage is activated via connection strings (`Dialer`, `Analytics`, `Realtime`, or fallback `Postgres`).

### Inbound Message Flow

```
Webhook (WhatsApp/SMS/WebChat/Email/Telegram/etc.)
  -> IWebhookHandler.HandleAsync
      -> WebhookResult: NewMessage | StatusUpdate | Ignored
  -> NewMessage path:
      -> IInboundMessagePipeline.ProcessAsync
          -> DeduplicateStep (IMessageStore.FindByExternalIdAsync)
          -> ContactResolutionStep (IContactIdentityResolver.ResolveAsync)
          -> ConversationResolutionStep (IConversationStore + IConversationLifecycleService)
          -> MessagePersistenceStep (IMessageStore.SaveAsync)
          -> PipelineResult (ConversationId, ContactId, MessageId, IsNewConversation)
      -> IInboundRouter.RouteAsync (optional)
          -> ChannelQueueMappingMiddleware
          -> LastAgentMiddleware
          -> PriorityEscalationMiddleware
          -> OverflowMiddleware
          -> BusinessHoursMiddleware
          -> RouteResult (QueueId, AgentId, Priority)
      -> IConversationSwitchboard.AssignToQueueAsync / OfferToAgentAsync
  -> StatusUpdate path:
      -> DeliveryStatusHandler.HandleAsync
          -> IMessageStore.FindByExternalIdAsync
          -> IMessageStore.UpdateDeliveryStatusAsync
```

### Middleware Pipeline

```
ErrorHandlingMiddleware -> CORS -> RateLimiter -> TenantResolutionMiddleware -> Authentication -> Authorization
```

### Auth & RBAC

- **Dual-scheme auth:** JWT (RS256) + API key (`X-Api-Key` header)
- **JWT flow:** Email/Password login -> JWT access token + refresh token
- **MFA:** TOTP (optional or required per tenant policy)
- **OIDC SSO:** OpenID Connect provider integration per tenant
- **API Keys:** Machine-to-machine, scoped, SHA-256 hashed
- **Sessions:** Idle + absolute timeout, revocation
- **Lockout:** Configurable threshold + duration per tenant
- **Password policies:** Min length, uppercase, number, special per tenant
- **RBAC:** 52 permissions (`domain:resource:action`), 7 role templates, custom roles per-tenant, permission cascading via `PermissionResolver` + `PermissionAuthorizationHandler`
- **Authorization policies:** `AdminOnly`, `SupervisorPlus`, `Authenticated`

### Endpoint Inventory (41 groups, 41 files)

All endpoints are in `src/Asterisk.Platform.Api/Endpoints/`. Key groups:

| Category | Endpoint Files |
|----------|---------------|
| Auth | AuthEndpoints, AuthAdminEndpoints, OidcEndpoints, RbacEndpoints |
| Omnichannel | WebhookEndpoints, ConversationEndpoints, ChannelConfigEndpoints, ContactEndpoints, SseEndpoints |
| Agent | AgentEndpoints, SupervisorEndpoints, SkillEndpoints, UsersMeEndpoint |
| Admin | AdminEndpoints, AuditEndpoints, ScheduledReportEndpoints |
| Management | ManagementTenantEndpoints, ManagementSystemEndpoints, ManagementClusterEndpoints, ManagementApiKeyEndpoints, SetupEndpoints |
| Dialer | CampaignEndpoints, CallAttemptEndpoints, DncListEndpoints, CallerIdPoolEndpoints, HolidayCalendarEndpoints, DialerSettingsEndpoints, TrunkEndpoints, OutboundRouteEndpoints |
| Analytics | AnalyticsEndpoints, AnalyticsLiveEndpoints, QueueMetricsEndpoints |
| AI/Bot | BotEndpoints, KnowledgeBaseEndpoints, AgentAssistEndpoints, FlowEndpoints |
| Media | MediaEndpoints, RecordingEndpoints |
| Realtime | RealtimeEndpoints, ClusterEndpoints |
| Other | DispositionEndpoints, SurveyEndpoints |

### Pro Package Integration

Platform.Api consumes 16 Pro NuGet packages:

```
Pro.Dialer + Pro.Dialer.Storage.Postgres    -- Outbound campaigns
Pro.EventStore + Pro.EventStore.Postgres     -- Event sourcing, CDR
Pro.Analytics + Pro.Analytics.Storage.Postgres -- Real-time metrics
Pro.CallAnalytics + Pro.CallAnalytics.Storage.Postgres -- Post-call AI
Pro.AgentAssist + Pro.AgentAssist.Storage.Postgres -- Live agent assist
Pro.Realtime + Pro.Realtime.Storage.Postgres -- Asterisk Realtime DB
Pro.Cluster                                  -- Multi-server clustering
Pro.MultiTenant                              -- Tenant isolation
Pro.Routing                                  -- Skill-based routing
Pro.Licensing                                -- License enforcement
```

## Docker Deployment

```sh
# Full stack: Asterisk 22 + Platform API + Web + Postgres + Redis + MinIO
docker compose -f docker/docker-compose.full.yml up

# Production (no dev seeds, external DB)
docker compose -f docker/docker-compose.production.yml up

# Demo environment (pre-seeded, simulated PSTN)
docker compose -f docker/demo/docker-compose.demo.yml up

# Dev (root-level, API only)
docker compose up
```

## DI Registration (Composition Root Pattern)

```csharp
// ── Core Platform ──
builder.Services.AddAsterisk(builder.Configuration);  // AMI + ARI
builder.Services.AddAsteriskSessions();                // Call session manager
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

// ── Storage ──
builder.Services.AddInMemoryStorage();  // zero-infrastructure default

// ── Pro packages (conditional on connection strings) ──
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
builder.Services.AddAsteriskMultiTenant();
builder.Services.AddProLicensing(o => o.EnforcementMode = EnforcementMode.Disabled);

// ── Auth (JWT RS256 + API key dual-scheme) ──
builder.Services.AddDynamicAuth(jwtTokenService);
builder.Services.AddSingleton<PermissionResolver>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
```

## Demo Environment

Full documentation at `docs/demo-environment.md`. **This file MUST be updated whenever any file under `docker/demo/` is modified** (scripts, compose, SQL seeds, configs, audio files).

## Code Conventions

- **No `Co-Authored-By` in commits**
- AOT: No reflection. `[JsonSerializable]`, `[LoggerMessage]`, static dispatch.
- Async-first with CancellationToken
- Private fields: `_camelCase`
- File-scoped namespaces (warning-level enforcement)
- Test naming: `Method_ShouldExpected_WhenCondition`
- Test stack: xunit 2.9.3, FluentAssertions 7.1.0, NSubstitute 5.3.0
- TreatWarningsAsErrors ON, WarningLevel 9999
- Central package management in Directory.Packages.props
- Key NuGet versions: Npgsql 9.0.3, Dapper 2.1.66, BCrypt.Net-Next 4.0.3, System.IdentityModel.Tokens.Jwt 8.7.0

## v1.1.0 "Enterprise Ready" -- COMPLETE (2026-03-26)

**Spec:** `docs/specs/2026-03-26-v110-enterprise-ready-design.md`
**Plans:** `docs/superpowers/plans/2026-03-26-plan20{a,b,c,d}-*.md`
**Result:** 78 tasks, 45 commits (20 Platform + 25 Platform.Web), 72 new backend tests, ~10 new frontend pages

Three pillars delivered:
1. **Auth Enterprise** -- Email/Password + JWT(RS256) + MFA(TOTP) + OIDC SSO + API Keys(M2M) + Auth Audit + Sessions + Lockout + Password Policies
2. **RBAC Granular** -- 52 permissions (`domain:resource:action`), 7 templates, custom roles per-tenant, permission cascading, PermissionGuard
3. **UI Completion** -- 29 hooks wired, delete confirmations (3s delay), route drag-and-drop, bulk import, diagnostics, audit trail

## Plan 24: Bug Fixes & Warnings -- COMPLETE (2026-03-30)

**Spec:** `docs/superpowers/specs/2026-03-30-plan24-bugfixes-design.md`
**Plan:** `docs/superpowers/plans/2026-03-30-plan24-bugfixes.md`

Three fixes:
1. **InMemory RBAC Stores** -- Added 4 missing RBAC stores to AddInMemoryStorage() (IUserRoleStore, IPermissionStore, IRoleTemplateStore, ITenantRoleStore)
2. **SDK AGI/ARI Hosted Services** -- Registered AgiHostedService, created+registered AriConnectionHostedService (SDK v1.5.2)
3. **Zero Warnings** -- 9 CA1822 fixes (static methods), 1 CA2012 suppression, TreatWarningsAsErrors=true restored

## Plan 25: Tenant Login Resolution -- COMPLETE (2026-03-30)

**Spec:** `docs/superpowers/specs/2026-03-30-tenant-login-resolution-design.md`
**Plan:** `docs/superpowers/plans/2026-03-30-plan25-tenant-login.md`

Progressive tenant resolution chain:
1. **Login fallback** -- accepts tenant from body OR middleware context (X-Tenant-Id header, subdomain)
2. **Subdomain prep** -- TenantResolutionMiddleware extracts subdomain (no-op on localhost, activates on wildcard DNS)
3. **Frontend env** -- VITE_DEFAULT_TENANT_ID for demo/single-tenant, subdomain extraction for SaaS
4. **Test infrastructure** -- removed Postgres connection strings from appsettings.json (Docker env vars only), fixed 51 missing [FromServices] on GET/DELETE endpoints

**Next:** v1.1.1 (SAML, IP allowlisting, subdomain routing, multi-language) -> v1.2.0 (SCIM, LDAP, WebAuthn, stereo, SignalR)

## Plan 26: Platform Administration — Sub-project A -- COMPLETE (2026-03-30)

**Spec:** `docs/superpowers/specs/2026-03-30-platform-admin-design.md`
**Plan:** `docs/superpowers/plans/2026-03-30-plan26-platform-admin.md`

Host tenant identity + Management API:
1. **TenantType + Hierarchy** -- TenantType enum (Platform/Partner/Customer), ParentTenantId, max depth 3
2. **Platform Permissions** -- 8 `platform:*` permissions, `platform_admin` role template (60 total permissions)
3. **PlatformAdminOnly auth** -- New authorization handler + policy for `/api/management/` endpoints
4. **Management API** -- Tenant CRUD, System info/license/settings, Cluster status/nodes, API key management
5. **Setup Wizard** -- `POST /api/setup` for first-boot platform initialization
6. **Management API Keys** -- `ApiKeyType.Management` for platform-scoped machine-to-machine access

## Plan 27: E2E Playwright Sprint 1 + Auth Event Filtering -- COMPLETE (2026-03-31)

**Spec:** `docs/superpowers/specs/2026-03-31-e2e-playwright-design.md`
**Plan:** `docs/superpowers/plans/2026-03-31-plan27-e2e-playwright.md`

Two deliverables:

1. **E2E Testing Infrastructure** -- Playwright in Platform.Web (`tests/e2e/`), auth fixture (storageState), API helper, ~100 data-testid attributes across 11 components, 10 spec files with 66 tests covering login + all Platform Admin pages
2. **Auth Event Reactive Filtering** -- `AuthEventQuery` record, `SearchAsync` on `IAuthEventStore` (InMemory + Postgres), endpoint accepts `eventType`/`startDate`/`endDate`, frontend debounce 300ms + `placeholderData` + `isFetching` + page reset, 11 new backend tests (1,068 total)

E2E roadmap: Sprint 1 done, Sprints 2-6 pending (Tenant Admin, Operations, Agent, Flows, Cross-Cutting -- ~330 total tests)

## Plan Execution

**Always use Subagent-Driven Development** with risk-weighted batching (FCM pattern):
- Phase A: Foundation (scaffolding, models) -- batch
- Phase B: Critical components (serializers, calculators) -- individual focused subagents
- Phase C: Integration (DI, storage, wiring) -- batch
