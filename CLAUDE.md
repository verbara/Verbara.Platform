# CLAUDE.md

## Project Overview

Asterisk.Platform is the API host and composition root for the omnichannel contact center. .NET 10 Native AOT. Consumes MIT SDK packages via NuGet (v1.5.4) and Pro packages (v1.1.1-pro).

**28 packages, 1396 tests, 0 warnings, AOT-compatible, 47 endpoint groups, version 1.3.1:**

| Package | Purpose | Tests |
|---------|---------|-------|
| Platform.Core | Abstractions, value objects, base interfaces, IClock, GDPR, Webhooks, DI | 42 |
| Platform.Identity | Users, RBAC, API keys, service accounts, OIDC SSO, DI | 26 |
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
| Platform.Audit | Audit trail -- event logging, query, retention, DI | 41 |
| Platform.Media | Media storage abstraction, FileSystem + S3 backends, recording options, DI | 10 |
| Platform.Billing | Metering engine, quota enforcement, rate cards, invoice generation, DI | 40 |
| Platform.Storage.InMemory | In-memory implementations of all stores -- dev/test, DI | 115 |
| Platform.Storage.Postgres | PostgreSQL implementations, RBAC seeder, Npgsql + Dapper | 6 |
| Platform.Api | HTTP host -- 47 endpoint groups, auth, middleware, SSE, OpenAPI | 410 |

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

`Program.cs` registers all platform packages, all Pro packages (Dialer, EventStore, Analytics, CallAnalytics, AgentAssist, Realtime, Cluster, MultiTenant, Licensing), configures auth (JWT + API key dual-scheme), RBAC, rate limiting, CORS, health checks, and maps 47 endpoint groups.

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
- **RBAC:** 64 permissions (`domain:resource:action`), 8 role templates (Agent, Supervisor, Quality Analyst, Manager, Admin, System Admin, API, Platform Admin), custom roles per-tenant, permission cascading via `PermissionResolver` + `PermissionAuthorizationHandler`
- **Authorization policies:** `AdminOnly`, `SupervisorPlus`, `Authenticated`

### Endpoint Inventory (43 groups, 43 files)

All endpoints are in `src/Asterisk.Platform.Api/Endpoints/`. As of v1.3.1, all routes are versioned under `/api/v1/` (Asp.Versioning.Http, URL-segment strategy). Legacy `/api/` paths redirect for backward compatibility. Key groups:

| Category | Endpoint Files |
|----------|---------------|
| Auth | AuthEndpoints, AuthAdminEndpoints, OidcEndpoints, RbacEndpoints |
| Omnichannel | WebhookEndpoints, ConversationEndpoints, ChannelConfigEndpoints, ContactEndpoints, SseEndpoints |
| Agent | AgentEndpoints, SupervisorEndpoints, SkillEndpoints, UsersMeEndpoint |
| Admin | AdminEndpoints, AuditEndpoints, ScheduledReportEndpoints |
| Management | ManagementTenantEndpoints, ManagementSystemEndpoints, ManagementClusterEndpoints, ManagementApiKeyEndpoints, ManagementBillingEndpoints, ManagementImpersonationEndpoints, ManagementWebhookEndpoints, SetupEndpoints |
| GDPR | GdprEndpoints |
| Webhooks (outbound) | WebhookSubscriptionEndpoints, WebhookEventTypeEndpoints |
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
Pro.Cluster + Pro.Cluster.Storage.Postgres   -- Multi-server clustering
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
builder.Services.AddPlatformBilling();

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
builder.Services.UsePostgresClusterTransport(clusterConn);  // conditional on connection string
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
- Key NuGet versions: Npgsql 9.0.3, Dapper 2.1.66, BCrypt.Net-Next 4.0.3, System.IdentityModel.Tokens.Jwt 8.7.0, Asp.Versioning.Http, QuestPDF, ScottPlot, MailKit, NCrontab
- **PostgreSQL 18** — all Docker compose files standardized on `postgres:18-alpine`
- **JWT claims** — `MapInboundClaims = false` in AddJwtBearer. All auth handlers use short claim names (`tid`, `role`, `sub`). .NET 10 maps claims by default; without this setting, `FindFirst("tid")` fails.
- **Npgsql 9 + Dapper:** Postgres row types MUST be class-based with `{get; init;}`, NOT positional records. Npgsql 9 returns `DateTime` for `timestamptz`; Dapper constructor matching fails with nullable `DateTime?` params. All 43 stores already converted.

## v1.1.0 "Enterprise Ready" -- COMPLETE (2026-03-26)

**Spec:** `docs/specs/2026-03-26-v110-enterprise-ready-design.md`
**Plans:** `docs/superpowers/plans/2026-03-26-plan20{a,b,c,d}-*.md`
**Result:** 78 tasks, 45 commits (20 Platform + 25 Platform.Web), 72 new backend tests, ~10 new frontend pages

Three pillars delivered:
1. **Auth Enterprise** -- Email/Password + JWT(RS256) + MFA(TOTP) + OIDC SSO + API Keys(M2M) + Auth Audit + Sessions + Lockout + Password Policies
2. **RBAC Granular** -- 64 permissions (`domain:resource:action`), 8 templates, custom roles per-tenant, permission cascading, PermissionGuard
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

**Next:** v1.2.0 "Monetization Ready" (billing, metering, quotas, rate cards, invoices, per-tenant analytics)

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

## v1.2.0 "Monetization Ready" -- COMPLETE (2026-03-31)

**Spec:** `docs/superpowers/specs/2026-03-31-v120-monetization-ready-design.md`

New package `Asterisk.Platform.Billing` with 4 sub-projects:
- **Sub-project A:** Metering Engine + Quota Enforcement -- COMPLETE (Plan 28A)
- **Sub-project B:** Rate Cards + Invoice Generation -- COMPLETE (Plan 28B)
- **Sub-project C:** Management API + Usage Dashboard -- COMPLETE (Plan 28C)
- **Sub-project D:** Frontend Pages + E2E Tests -- COMPLETE (Plans 28D+28E, in Platform.Web)

## Plan 28A: Metering Engine + Quota Enforcement -- COMPLETE (2026-03-31)

**Spec:** `docs/superpowers/specs/2026-03-31-v120-monetization-ready-design.md` (Sub-project A)
**Plan:** `docs/superpowers/plans/2026-03-31-plan28a-metering-engine.md`

New package `Asterisk.Platform.Billing` (28th package):
1. **Domain Models** -- UsageType (17 values), UsageUnit (6 values), UsageRecord, UsageSummary, TenantQuota, QuotaAction, QuotaCheckResult
2. **Services** -- DefaultMeteringService (record + batch + current-period summary), DefaultQuotaEnforcementService (limit check per UsageType with Warn/SoftBlock/HardBlock)
3. **InMemory Storage** -- InMemoryUsageRecordStore (append-only, summary aggregation), InMemoryTenantQuotaStore (CRUD)
4. **Postgres Storage** -- PostgresUsageRecordStore (GROUP BY aggregation), PostgresTenantQuotaStore (UPSERT), 002_BillingSchema.sql migration
5. **DI + Wiring** -- AddPlatformBilling(), stores in both AddInMemoryStorage() and AddPostgresStorage(), wired in Program.cs

Key models: UsageRecord (16 types), UsageSummary, TenantQuota (MaxConcurrentChannels, MaxActiveCampaigns, MaxMonthlyVoiceMinutes, MaxMonthlyMessages, MaxStorageBytes, MaxActiveAgents)

Key models: UsageRecord (16 types), UsageSummary, TenantQuota, RateCard, Invoice
Key fixes: OriginateGate tenant limit (broken), campaign count enforcement (missing)

## Plan 28B: Rate Cards + Invoice Generation -- COMPLETE (2026-03-31)

**Spec:** `docs/superpowers/specs/2026-03-31-v120-monetization-ready-design.md` (Sub-project B)
**Plan:** `docs/superpowers/plans/2026-03-31-plan28b-rate-cards-invoices.md`

Extends Platform.Billing package with pricing and invoicing:
1. **Domain Models** -- RateCard (with RateEntry + RateTier), Invoice (with InvoiceLineItem), InvoiceStatus enum
2. **Store Interfaces** -- IRateCardStore (CRUD + active lookup), IInvoiceStore (CRUD + pagination + status transitions)
3. **Invoice Generation** -- DefaultInvoiceGenerationService with flat-rate pricing (included quantities, overage) and tiered pricing
4. **InMemory Storage** -- InMemoryRateCardStore, InMemoryInvoiceStore
5. **Postgres Storage** -- PostgresRateCardStore (UPSERT, JSONB rates), PostgresInvoiceStore (JSONB line_items, status CASE), 003_RateCardsInvoices.sql migration
6. **DI Wiring** -- IInvoiceGenerationService in AddPlatformBilling(), stores in both storage packages

## Plan 28C: Management Billing API -- COMPLETE (2026-03-31)

**Spec:** `docs/superpowers/specs/2026-03-31-v120-monetization-ready-design.md` (Sub-project C)
**Plan:** `docs/superpowers/plans/2026-03-31-plan28c-management-billing-api.md`

Management API for billing administration (PlatformAdminOnly):
1. **Rate Card CRUD** -- List, Create, Update, Delete rate cards per tenant
2. **Invoice Management** -- List, Generate, Get, Issue invoices per tenant
3. **Usage Queries** -- Summary and detailed usage records with date range and type filters
4. **Quota Management** -- View quota status with current usage, update tenant quotas
5. **Store Extension** -- Added ListAsync to IUsageRecordStore (InMemory + Postgres) for paginated record queries

## Plan 28D+28E: Billing Frontend + E2E Tests -- COMPLETE (2026-03-31)

Delivered in Platform.Web repo:
- **4 billing pages** (rate cards CRUD, invoices, usage dashboard, quotas) under `/admin/billing/*`
- **1 API hooks file** (`use-billing.ts`) with 15 TanStack Query hooks
- **25 E2E tests** across 4 spec files (90 total E2E tests, 14 spec files)
- ApiHelper extended with 9 billing methods for test data seeding
- Fix: billing pages fallback to auth tenant when no active tenant selected

## v1.2.1 "Operations" -- COMPLETE (2026-03-31)

5 sub-projects across 3 repos (Platform, SDK Pro, Platform.Web):

- **Plan 29A:** DTO Hardening -- COMPLETE
- **Plan 29B:** PostgresClusterTransport (SDK Pro) -- COMPLETE
- **Plan 29C:** Server Management API -- COMPLETE
- **Plan 29D:** Impersonation -- COMPLETE
- **Plan 29E:** Cluster UI (Platform.Web) -- COMPLETE

## Plan 29A: DTO Hardening -- COMPLETE (2026-03-31)

Replaced 61 anonymous `new {}` response objects with typed sealed records:
1. **Shared DTOs** -- ErrorResponse, MessageResponse, StatusUpdateResponse in `Endpoints/Shared/`
2. **Per-file DTOs** -- 9 endpoint-specific DTO records (e.g., ClusterStatusDto, SystemInfoDto)
3. **ApiJsonContext** -- All new DTOs registered for AOT serialization
4. **14 endpoint files refactored** -- type-safe responses throughout

## Plan 29B: PostgresClusterTransport (SDK Pro) -- COMPLETE (2026-03-31)

New `Asterisk.Sdk.Pro.Cluster.Storage.Postgres` package:
1. **PostgresClusterTransport** -- Implements all 19 abstract methods from ClusterTransportBase
2. **6 PostgreSQL tables** -- cluster_nodes, cluster_instances, cluster_session_snapshots, cluster_drain_states, cluster_locks, cluster_generations
3. **LISTEN/NOTIFY** -- PostgreSQL pub/sub for real-time cluster events
4. **EnsureSchemaAsync** -- Auto-migration on startup
5. **NodeUpdate** -- New record + UpdateNodeAsync on ClusterManager and all transports
6. **DI** -- `UsePostgresClusterTransport(connectionString)`

## Plan 29C: Server Management API -- COMPLETE (2026-03-31)

6 new PlatformAdminOnly endpoints in ManagementClusterEndpoints:
1. **POST /api/management/cluster/nodes** -- Add node
2. **PUT /api/management/cluster/nodes/{nodeId}** -- Update node
3. **DELETE /api/management/cluster/nodes/{nodeId}** -- Remove node
4. **DELETE /api/management/cluster/drain/{nodeId}** -- Cancel drain
5. **POST /api/management/cluster/drain/{nodeId}/force** -- Force drain
6. **GET /api/management/cluster/instances** -- List platform instances
7. **Updated DTOs** -- MgmtClusterStatusDto with Instances, MgmtDrainStatusDto with EstimatedTimeToZero
8. **Conditional wiring** -- PostgresClusterTransport in Program.cs when Cluster connection string present

## Plan 29D: Impersonation -- COMPLETE (2026-03-31)

Shadow JWT impersonation for platform administrators:
1. **ManagementImpersonationEndpoints** -- POST /api/management/impersonate, DELETE /api/management/impersonate (43rd endpoint group)
2. **JwtTokenService.GenerateImpersonationToken()** -- Shadow JWT with 30-min TTL, impersonator_id, impersonator_tenant, impersonation=true claims
3. **Middleware restrictions** -- Blocks tenant delete, recursive impersonation, system settings, setup during impersonation
4. **Auth events** -- impersonation_started, impersonation_ended event types
5. **Frontend** -- useImpersonate/useEndImpersonate hooks, auth store with impersonation state (save/restore original token), ImpersonationBanner with countdown timer

## Plan 29E: Cluster UI (Platform.Web) -- COMPLETE (2026-03-31)

Dedicated cluster management page in Platform.Web:
1. **Cluster page** -- /admin/cluster with DataTable, summary cards, CRUD sheets (add/edit node)
2. **Drain management** -- Drain dialog, ConfirmDeleteDialog for remove/force, active drains section (amber)
3. **Platform instances** -- Instances section in cluster page
4. **use-cluster.ts rewrite** -- Fixed path mismatch (/api/admin/ -> /api/management/)
5. **Sidebar** -- Network icon entry for cluster page
6. **Consolidation** -- Cluster info removed from diagnostics-page and system-page

## v1.3.0 "Integration & Compliance" -- COMPLETE (2026-04-01)

**Spec:** `docs/superpowers/specs/2026-04-01-v130-integration-compliance-design.md`

4 sub-projects removing critical production blockers:
- **Sub-project A:** License Enforcement -- COMPLETE (Plan 30A)
- **Sub-project B:** OIDC SSO Completion -- COMPLETE (Plan 30B)
- **Sub-project C:** GDPR Compliance -- COMPLETE (Plan 30C)
- **Sub-project D:** Outbound Webhooks -- COMPLETE (Plan 30D)

## Plan 30A: License Enforcement -- COMPLETE (2026-04-01)

Activate existing ECDSA P-256 licensing with periodic runtime re-validation:
1. **ILicenseStatus** -- queryable singleton interface (SDK Pro)
2. **LicenseStatusTracker** -- thread-safe implementation updated by hosted services (SDK Pro)
3. **LicenseRevalidationService** -- timer-based re-validation every 6h (SDK Pro)
4. **Config-driven Program.cs** -- WarnOnly in dev, Enforce in production, community mode without license
5. **Management API enrichment** -- GET /api/management/system/license returns full license state

## Plan 30B: OIDC SSO Completion -- COMPLETE (2026-04-01)

Complete the OIDC callback with Authorization Code + PKCE + nonce validation:
1. **OidcTokenExchangeService** -- token endpoint + JWKS discovery with 24h cache, key rotation retry
2. **OidcUserProvisioningService** -- subject lookup, email fallback linking, auto-create with default role
3. **OidcEndpoints rewrite** -- PKCE S256, nonce, DataProtection-encrypted state cookie (5min TTL)
4. **User.OidcSubject** -- new field + FindByOidcSubjectAsync on IUserStore (InMemory + Postgres)
5. **Migration 004** -- oidc_subject column + partial index on users table

## Plan 30C: GDPR Compliance -- COMPLETE (2026-04-01)

Data export, purge with tombstone, and retention policies:
1. **GdprExportService** -- JSON export of contact + conversations + messages + auth events + audit
2. **GdprPurgeService** -- cascade delete (messages → conversations → auth events → contact) + tombstone
3. **RetentionPurgeService** -- 24h background job for automatic data expiration per tenant
4. **5 store interfaces extended** -- delete/list methods on Conversation, Message, AuthEvent, Audit, UsageRecord
5. **5 GDPR endpoints** -- export (AdminOnly), purge (AdminOnly), purge-log, retention GET/PUT (PlatformAdminOnly)
6. **Migration 005** -- purge_log + tenant_retention_policies tables

## Plan 30D: Outbound Webhooks -- COMPLETE (2026-04-01)

Tenant event subscriptions with persistent delivery and dead-letter queue:
1. **WebhookDispatcher** -- subscribes to PlatformEventBus, routes events to tenant subscriptions
2. **WebhookDeliveryService** -- background worker, exponential backoff (8 attempts, ~24h), HMAC-SHA256
3. **11 event types** -- conversation.*, agent.*, campaign.*, agentassist.* (dot-separated)
4. **13 endpoints** -- 8 tenant subscription CRUD, 2 management DLQ, 1 event types, POST test, rotate-secret
5. **Migration 006** -- webhook_subscriptions + webhook_deliveries tables with partial indexes

## Docker Demo Fixes -- COMPLETE (2026-04-02)

Postgres Npgsql 9 + Dapper compatibility fix for Docker demo:
1. **40 Postgres stores** -- All row types converted from positional records to class-based `{get; init;}` for Dapper property mapping
2. **DateTimeOffset → DateTime** -- All timestamp fields in row types (Npgsql 9 returns DateTime for timestamptz)
3. **Demo fully operational** -- `demo-reset.sh` seeds all data, login works, API healthy
4. **E2E: 115/202 passed** -- Remaining 87 failures are UI-level test selector issues, not API/DB problems
5. **SDK Pro bumped** -- v1.0.0-pro → v1.1.0-pro (ILicenseStatus, LicenseRevalidation, Cluster.Storage.Postgres)

## v1.3.1 "Operational Maturity" -- COMPLETE (2026-04-04)

**Spec:** `docs/superpowers/specs/2026-04-04-v131-operational-maturity-design.md`

7 deliverables hardening the platform for production operations:

1. **API Versioning** -- Hybrid Pragmatic strategy via `Asp.Versioning.Http`. URL segment (`/api/v1/`), additive-only policy, preview namespace for unstable endpoints. Backward-compat redirect from legacy `/api/` paths.
2. **Per-Tenant Rate Limiting** -- 5 tiers: Free / Standard / Professional / Enterprise / Unlimited. `RateLimitTier` on `TenantSettings`, enforced in `RateLimitMiddleware` via `IRateLimitStore`.
3. **Audit Expansion** -- Okta-inspired schema: 5 categories (auth, data, admin, billing, system), typed `ActorId`/`TargetId`, `Before`/`After` JSON diffs. Backward-compatible with existing `IAuditStore`.
4. **License Gates** -- `RequireLicenseFeature` attribute on Pro feature endpoints. `EnforcementMode` (WarnOnly / Enforce) governs behavior; platform admin bypasses gates.
5. **Scheduled Reports** -- `ReportSchedulerService` (`IHostedService`, NCrontab-based). Generates PDF (QuestPDF) + chart images (ScottPlot), delivers via SMTP (MailKit). Postgres-backed schedule store with `ScheduledReportEndpoints`.
6. **Webhook Circuit Breaker** -- 3-state FSM (Closed → Open → HalfOpen) per subscription. Exponential backoff escalation, admin reset endpoint `POST /api/v1/management/webhooks/{id}/reset-circuit`.
7. **GDPR Enhancements** -- CSV export: ZIP with 6 CSVs, bilingual headers (EN/ES), UTF-8 BOM. User purge: `purge-preview` dry-run endpoint + `X-Confirm-Purge` header guard, pseudonymization in audit log.

### New DI registrations (v1.3.1)

```csharp
// Rate limiting (per-tenant tiers)
builder.Services.AddPlatformRateLimiting();

// Scheduled reports (NCrontab + QuestPDF + MailKit)
builder.Services.AddPlatformScheduledReports(o =>
{
    o.SmtpHost = config["Reports:SmtpHost"];
    o.SmtpPort = int.Parse(config["Reports:SmtpPort"]!);
    o.FromAddress = config["Reports:FromAddress"];
});

// API versioning
builder.Services.AddPlatformApiVersioning();  // wired in Program.cs
```

## v1.3.1 Web Sync + GDPR Fix -- COMPLETE (2026-04-05)

Two stabilization commits:
1. **API URL migration** -- Updated all 55 Platform.Web files (38 hooks, 6 auth pages, API client, SSE, vite proxy, config.json, E2E fixtures) from `/api/` to `/api/v1/`, eliminating reliance on VersionRedirectMiddleware
2. **GDPR export fix** -- Made `[FromQuery] string format` nullable on `ExportContactData` endpoint, fixing 2 pre-existing test failures (non-nullable string treated as required by ASP.NET model binding)

## Docker & Storage Stabilization -- COMPLETE (2026-04-06)

Six commits fixing Docker deployment and storage gaps:
1. **Full-stack compose fixes** -- Install curl in Dockerfile for healthcheck, disable licensing in dev, robust healthcheck with start_period
2. **Docker image upgrades** -- PostgreSQL 16/17→18-alpine, Redis 7→8-alpine across all 4 compose files
3. **PostgresTenantStore** -- New Dapper implementation of ITenantStore (was InMemory-only, tenants lost on restart). Migration 007 adds tenants + scheduled_reports + report_executions tables
4. **Pro Analytics InMemory fallbacks** -- InMemoryCompletedSessionStore, InMemoryIntervalSnapshotStore, InMemoryCallAnalyticsStore prevent DI crashes when Analytics connection string is absent

## Auth & Deployment Fixes -- COMPLETE (2026-04-07)

Four commits fixing critical auth and deployment issues discovered during full-stack testing:
1. **JWT MapInboundClaims** -- .NET 10 JWT Bearer maps claims by default (`tid`→full URI), breaking PlatformAdminOnly authorization. Set `MapInboundClaims = false` explicitly + role claim fallback in both auth handlers.
2. **API Key key_type** -- Added `key_type` column to api_keys table (migration 008), PostgresApiKeyStore now persists `ApiKeyType.Management`. Management API keys work correctly from Postgres.
3. **Dynamic version** -- Replaced hardcoded "1.3.0" in SystemInfoDto with `AssemblyInformationalVersionAttribute` read from `<Version>1.3.1</Version>` in csproj.
4. **Web tenant resolution** -- `VITE_DEFAULT_TENANT_ID` build arg in Platform.Web Dockerfile + docker-compose.full.yml for localhost login.

## Plan Execution

**Always use Subagent-Driven Development** with risk-weighted batching (FCM pattern):
- Phase A: Foundation (scaffolding, models) -- batch
- Phase B: Critical components (serializers, calculators) -- individual focused subagents
- Phase C: Integration (DI, storage, wiring) -- batch
