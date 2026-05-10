using Asp.Versioning;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Identity.Auth;
using Microsoft.AspNetCore.DataProtection;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using Microsoft.AspNetCore.RateLimiting;
using Verbara.Platform.Bot;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Core.DependencyInjection;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Storage.Postgres;
using Verbara.Platform.Audit;
using Verbara.Platform.Media;
using Verbara.Platform.Switchboard;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.DataProtection;
using Verbara.Platform.Identity.DependencyInjection;
using Verbara.Platform.Identity.OidcTokenExchange;
using Verbara.Platform.Identity.Redis.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Sdk.Hosting;
using Verbara.Platform.KnowledgeBase;
using Verbara.Platform.Surveys;
using Verbara.Platform.Billing;
using Verbara.Platform.Core.Reports;
using Verbara.Sdk.Pro.Dialer.DependencyInjection;
using Verbara.Sdk.Pro.Dialer.Storage.Postgres.DependencyInjection;
using Verbara.Sdk.Pro.EventStore.DependencyInjection;
using Verbara.Sdk.Pro.EventStore.Postgres.DependencyInjection;
using Verbara.Sdk.Pro.Analytics.DependencyInjection;
using Verbara.Sdk.Pro.Analytics.Live;
using Verbara.Sdk.Pro.Analytics.Storage.Postgres.DependencyInjection;
using Verbara.Sdk.Pro.Analytics.Storage.Postgres.Live;
using Verbara.Sdk.Pro.CallAnalytics.DependencyInjection;
using Verbara.Sdk.Pro.CallAnalytics.Storage.Postgres.DependencyInjection;
using Verbara.Sdk.Pro.AgentAssist.DependencyInjection;
using Verbara.Sdk.Pro.AgentAssist.Storage.Postgres.DependencyInjection;
using Verbara.Sdk.Pro.Licensing.DependencyInjection;
using Verbara.Sdk.Resilience.DependencyInjection;
using Verbara.Sdk.Pro.Storage.Common.Retention.DependencyInjection;
using Verbara.Sdk.Pro.Dialer.Storage.Postgres.Retention;
using Verbara.Sdk.Pro.EventStore.Postgres.Retention;
using Verbara.Sdk.Pro.Analytics.Storage.Postgres.Retention;
using Verbara.Sdk.Pro.CallAnalytics.Storage.Postgres.Retention;
using Verbara.Sdk.Pro.AgentAssist.Storage.Postgres.Retention;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Pro.AgentAssist.Engine;
using Verbara.Sdk.Pro.Routing.Skills;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.DependencyInjection;
using Verbara.Sdk.Pro.Realtime.Storage.Postgres.DependencyInjection;
using Verbara.Sdk.Pro.Realtime.Models;
using Verbara.Sdk.Pro.Realtime.Decorators;
using Verbara.Sdk.Pro.Realtime.Engine;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.Dialer.Storage.Postgres;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Pro.Cluster;
using Verbara.Sdk.Pro.Cluster.DependencyInjection;
using Verbara.Sdk.Pro.Cluster.Storage.Postgres.DependencyInjection;
using Verbara.Platform.Channels.WebChat;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Pro.MultiTenant.DependencyInjection;
using Verbara.Sdk.Push.Hosting;
using Verbara.Sdk.Push.Authz;
using Verbara.Sdk.Pro.Push.SignalR.DependencyInjection;
using Verbara.Sdk.Pro.Push.SignalR.Hubs;
using Verbara.Sdk.Pro.Push.SignalR.Bridges;
using Verbara.Sdk.Pro.Push.SignalR.Presence;
using Verbara.Sdk.Pro.Storage.Common.Retention;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Verbara.Platform.Api.Hubs;
using Verbara.Sdk.OpenTelemetry;
using Verbara.Sdk.Pro.OpenTelemetry;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ─── Verbara SDK Connection (Multi-Server + Sessions via Cluster) ───────────

builder.Services.AddVerbaraMultiServer();
builder.Services.AddVerbaraSessionsMultiServer();

// ─── Core Platform Services ──────────────────────────────────────────────────

// Verbara.Sdk.Push: in-process push event bus + delivery filter abstractions.
// MUST precede AddPlatformCore() so the IPushEventBus dependency is available
// for the PlatformEventBus DI ctor and the platform-specific delivery filter
// can override the SDK default.
builder.Services.AddVerbaraPush();
// Verbara.Sdk.Pro.Push.SignalR: PlatformHub + Phoenix-style Presence CRDT.
// Registers PresenceTracker (singleton), heartbeat + merge HostedServices,
// topic registration, SignalR server with ProPresenceJsonContext JSON resolver.
builder.Services.AddVerbaraProPushSignalR(o =>
{
    var clusterNodeId = builder.Configuration["Asterisk:ClusterNodeId"];
    if (!string.IsNullOrEmpty(clusterNodeId))
        o.NodeId = clusterNodeId;
});
// T27 event bridges (v1.8.0-pro): opt-in HostedServices that publish cluster /
// conversation / agent state transitions to IPushEventBus so cross-node consumers
// (SignalR clients, webhook subscribers, SSE listeners) observe state changes in
// real time. Each bridge throttles per key (node/conversation/agent) — see
// BridgeOptions for tuning knobs. DefaultTenantId only applies when ambient
// TenantContext.Current is unset (background SDK events without a request scope).
builder.Services
    .WithClusterEventBridge()
    .WithConversationBridge(opt => opt.DefaultTenantId = "default-tenant")
    .WithAgentBridge();

// Relay T27 bus events (conversation + agent state) to SignalR tenant groups.
builder.Services.AddHostedService<PushToHubRelay>();

// Override the SDK default AllowAllSubscriptionAuthorizer with RBAC-aware authorizer.
builder.Services.AddSingleton<ISubscriptionAuthorizer, RbacSubscriptionAuthorizer>();
// Replace Pro.Push.SignalR's NotImplementedSupervisorCoordinator default with the
// Platform concrete impl that delegates to AgentAssistSupervisor.
builder.Services.AddSingleton<ISupervisorCoordinator, PlatformSupervisorCoordinator>();
// R5.2 P0.6 — cross-tenant subscription validation per ADR-0005.
// IAgentTenantResolver: Postgres-backed, 5-minute IMemoryCache. Hub method
// SubscribeToAgentPresenceAsync queries this resolver to validate that the
// caller's JWT `tid` claim equals the agent's resolved tenant before joining
// the presence:agent:{agentId} group.
// IHubAuditSink: bridges hub.cross_tenant_subscription_denied entries into
// the Platform audit pipeline for SOC operators / SIEM correlation.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<Verbara.Sdk.Pro.Push.SignalR.Authz.IAgentTenantResolver,
    Verbara.Platform.Api.Authz.CachedAgentTenantResolver>();
builder.Services.AddSingleton<Verbara.Sdk.Pro.Push.SignalR.Authz.IHubAuditSink,
    Verbara.Platform.Api.Authz.PlatformHubAuditSink>();
// R5.2 PC.5 / B.12 — debounced last-used stamp for API keys. Backed by the
// shared IMemoryCache registered above; ≤ 1 UPDATE per minute per key per
// process. Fire-and-forget from ApiKeyAuthenticationHandler so auth latency
// is independent of the database write. (TimeProvider.System is registered
// elsewhere in Program.cs via TryAddSingleton — we don't double-register.)
builder.Services.AddSingleton<
    Verbara.Platform.Api.Auth.IApiKeyLastUsedStamper,
    Verbara.Platform.Api.Auth.ApiKeyLastUsedStamper>();
builder.Services.AddPlatformCore();
builder.Services.AddPlatformConversations();
builder.Services.AddPlatformChannels();
builder.Services.AddPlatformQueues();
builder.Services.AddInboundRouting();
builder.Services.AddSwitchboard();
builder.Services.AddPlatformBot();
builder.Services.AddHostedService<Verbara.Platform.Api.Services.BotAnalyticsPersistenceService>();
builder.Services.AddPlatformAudit();
builder.Services.AddPlatformMedia();
builder.Services.AddPlatformKnowledgeBase();
builder.Services.AddPlatformSurveys();
builder.Services.AddPlatformBilling();
builder.Services.AddWebChat();

// ─── Twilio SMS (conditional on config) ─────────────────────────────────────
var twilioSection = builder.Configuration.GetSection("Twilio");
if (!string.IsNullOrEmpty(twilioSection["AccountSid"]))
{
    builder.Services.Configure<Verbara.Platform.Channels.Sms.Providers.TwilioOptions>(o =>
    {
        o.AccountSid = twilioSection["AccountSid"]!;
        o.AuthToken = twilioSection["AuthToken"]!;
    });
    builder.Services.AddHttpClient("twilio");
    builder.Services.AddSingleton<Verbara.Platform.Channels.Sms.ISmsProvider,
        Verbara.Platform.Channels.Sms.Providers.TwilioSmsProvider>();
    // Transient-retry policy for Twilio HTTP calls (v1.9.1 Frente A).
    Verbara.Platform.Channels.Sms.ServiceCollectionExtensions.AddTwilioResiliencePolicy(builder.Services);
}

// ─── GDPR Services ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<IGdprExportService, GdprExportService>();
builder.Services.AddSingleton<IGdprPurgeService, GdprPurgeService>();
builder.Services.AddKeyedSingleton<IGdprExportFormatter, JsonGdprExportFormatter>("json");
builder.Services.AddKeyedSingleton<IGdprExportFormatter, CsvGdprExportFormatter>("csv");
builder.Services.AddHostedService<RetentionPurgeService>();
builder.Services.AddHostedService<AuditRetentionService>();

// AHH Phase 2: deferred-write queue for the login success path.
// AccountLockoutService.ResetAttemptsAsync, EnqueueLastLoginAtUpdateAsync,
// and AuthEventService.EnqueueLogSuccess all route through this BackgroundService
// so the request critical path doesn't pay 3 sync DB round-trips per login.
// Failure-path audit logs stay sync per ADR-0011. See Phase 2 plan section.
builder.Services.AddSingleton<AuthWriteQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuthWriteQueue>());

// ─── Storage ─────────────────────────────────────────────────────────────────
// ADR-0015 Phase 1 — wrap every connection string read with
// ConnectionStringDefaults.ApplyPoolDefaults so the NpgsqlDataSources across
// Platform.Storage.Postgres + the Pro storage packages inherit SMB-tier pool
// sizing (Maximum Pool Size=10, Minimum=2, Idle=300) when the operator hasn't
// specified an explicit value. Operator-set values pass through verbatim.
//
// ADR-0015 Phase 2 — build ONE NpgsqlDataSource per distinct connection string
// and share it across Platform + every Pro Use*/Add* call. Collapses the
// 14-pool sprawl identified in R5.5 Phase C-L into 1 pool per distinct conn
// string. Pro 1.16.0-pro / ADR-0008 supplies the `(NpgsqlDataSource)` overload
// surface that consumes the shared instances.
var coreConnectionString = ConnectionStringDefaults.ApplyPoolDefaults(
    builder.Configuration.GetConnectionString("Postgres"));
NpgsqlDataSource? sharedCoreDataSource = null;
if (!string.IsNullOrEmpty(coreConnectionString))
{
    sharedCoreDataSource = new NpgsqlDataSourceBuilder(coreConnectionString).Build();
    builder.Services.AddPostgresStorage(sharedCoreDataSource);

    // Apply Platform SQL migrations eagerly (before Pro EnsureSchemaAsync which references Platform tables)
    Verbara.Platform.Api.Services.DatabaseMigrationService.ApplyMigrations(coreConnectionString);

    // Override in-memory capacity with persistent version for restart recovery
    builder.Services.AddSingleton<IAgentCapacityService>(sp =>
        new PersistentAgentCapacityService(
            sp.GetRequiredService<IAgentStore>(),
            sp.GetRequiredService<IAgentCapacityStore>(),
            sp.GetRequiredService<IConversationStore>()));
}
else
{
    builder.Services.AddInMemoryStorage();
}

// ADR-0015 Phase 2 helper — return the shared core DataSource when the supplied
// connection string matches the core (most common deployment shape) so all Pro
// packages share one pool; otherwise build a dedicated DataSource for the
// distinct connection string (still a single instance per distinct conn string,
// not per package).
NpgsqlDataSource? ResolveDataSource(string? candidateConnectionString)
{
    if (string.IsNullOrEmpty(candidateConnectionString))
    {
        return null;
    }
    if (sharedCoreDataSource is not null && string.Equals(candidateConnectionString, coreConnectionString, StringComparison.Ordinal))
    {
        return sharedCoreDataSource;
    }
    return new NpgsqlDataSourceBuilder(candidateConnectionString).Build();
}

// ─── AHH Phase 1: Auth Hot-Path Caching ─────────────────────────────────────
// Decorates IUserStore + ITenantAuthConfigStore with IMemoryCache wrappers.
// Removes 5–10 ms × 2–3 DB round-trips from POST /auth/login on cache hit.
// Cross-replica invalidation engages later (after AddVerbaraPlatformIdentityRedis).
// See docs/plans/active/2026-04-27-auth-hotpath-hardening.md Phase 1 + ADR-0010.
builder.Services.AddAuthHotpathCaching();

// ─── IP Allowlist caching ─────────────────────────────────────────────────────
// IMemoryCache decorator over ITenantIpAllowlistStore. Mirrors the AHH pattern;
// cross-replica invalidation is not wired because allowlist mutations are
// operator-driven (rare) and 60s staleness is acceptable per the spec.
builder.Services.AddSingleton<CachedTenantIpAllowlistStore>(sp => new CachedTenantIpAllowlistStore(
    sp.GetRequiredKeyedService<ITenantIpAllowlistStore>(AuthHotpathCacheKeys.IpAllowlistStoreInner),
    sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));
{
    // Replace the unkeyed alias (direct keyed delegate, registered by
    // AddPostgresStorage / AddInMemoryStorage) with the cache decorator.
    for (var i = builder.Services.Count - 1; i >= 0; i--)
    {
        var d = builder.Services[i];
        if (d.ServiceType == typeof(ITenantIpAllowlistStore) && !d.IsKeyedService)
            builder.Services.RemoveAt(i);
    }
}
builder.Services.AddSingleton<ITenantIpAllowlistStore>(sp =>
    sp.GetRequiredService<CachedTenantIpAllowlistStore>());
builder.Services.AddSingleton<IIpAllowlistEvaluator, DefaultIpAllowlistEvaluator>();

// ─── Pro.Licensing ───────────────────────────────────────────────────────────
var licenseConfig = builder.Configuration.GetSection("Licensing");
var licensePath = licenseConfig["FilePath"] ?? "./license.lic";
var publicKeyPath = licenseConfig["PublicKeyPath"];
var licensePublicKey = !string.IsNullOrEmpty(publicKeyPath) && File.Exists(publicKeyPath)
    ? File.ReadAllBytes(publicKeyPath)
    : Array.Empty<byte>();
builder.Services.AddSingleton(licensePublicKey);

var enforcementMode = Enum.TryParse<Verbara.Sdk.Pro.Licensing.EnforcementMode>(
    licenseConfig["EnforcementMode"], ignoreCase: true, out var parsedMode)
    ? parsedMode
    : (builder.Environment.IsDevelopment()
        ? Verbara.Sdk.Pro.Licensing.EnforcementMode.WarnOnly
        : Verbara.Sdk.Pro.Licensing.EnforcementMode.Enforce);

// If no license file exists and no explicit config, fall back to WarnOnly (community mode)
if (!File.Exists(licensePath) && !licenseConfig.Exists())
    enforcementMode = Verbara.Sdk.Pro.Licensing.EnforcementMode.WarnOnly;

builder.Services.AddProLicensing(o =>
{
    o.LicenseFilePath = licensePath;
    o.EnforcementMode = enforcementMode;
    o.RevalidationInterval = TimeSpan.TryParse(licenseConfig["RevalidationInterval"], out var interval)
        ? interval
        : TimeSpan.FromHours(6);
});

// ─── Observability — OpenTelemetry tracing + metrics providers ──────────────
// Enrols every SDK ActivitySource + Meter (incl. Verbara.Sdk.Resilience) plus
// the 10 Pro ActivitySources + 15 Pro meters. Prometheus scraping endpoint
// mapped below via app.MapPrometheusScrapingEndpoint(). OTLP exporter opt-in
// via OTEL_EXPORTER_OTLP_ENDPOINT environment variable at runtime.
builder.Services.AddVerbaraOpenTelemetry(b => b
    .WithAllSources()
    .AddVerbaraProOpenTelemetry()
    // R5.5 finding: SDK WithAllSources() registers only Verbara-domain
    // meters. The standard ASP.NET Core HTTP server + Kestrel + System.Net.Http
    // meters carry the http_server_request_duration_seconds + http_client_*
    // histograms that the SLO targets reference. Without these the /metrics
    // endpoint cannot expose HTTP latency at all.
    .AddMeter("Microsoft.AspNetCore.Hosting")
    .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
    .AddMeter("Microsoft.AspNetCore.Routing")
    .AddMeter("System.Net.Http")
    .WithPrometheusExporter());

// ─── Pro Hardening — Resilience + LicenseGuard + Retention ──────────────────
// Resilience: meter + TimeProvider for circuit breaker / retry / timeout
// primitives (Verbara.Sdk.Resilience MIT, migrated from Pro.Resilience via
// ADR-0029 in Pro 1.9.0-pro).
builder.Services.AddVerbaraResilience();

// LicenseGuard: runtime feature check (10s cache + 7d grace by default)
builder.Services.AddProLicenseGuard();

// Retention: orchestrator (DryRun=true by default — flip off in production)
builder.Services.AddProRetention();

// R5.2 PC.1 — admin retention surface (in-process tracker + DryRun toggle).
builder.Services.AddSingleton<Verbara.Platform.Api.Endpoints.Retention.RetentionAdminState>();
builder.Services.AddSingleton<Verbara.Platform.Api.Endpoints.Retention.RetentionExecutionTracker>();
builder.Services.AddSingleton<
    Verbara.Platform.Api.Endpoints.Retention.IRetentionAdminService,
    Verbara.Platform.Api.Endpoints.Retention.RetentionAdminService>();

// ─── Resilience Policies (v1.9.1 — Frente B/E wraps) ────────────────────────
// flow.http-request: circuit 3/60s + retry 2/500ms + timeout 60s (upper bound).
// Per-call timeout is sourced from flow config, not policy — see HttpRequestNodeHandler.
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    Verbara.Platform.Flows.Nodes.HttpRequestNodeHandler.ResiliencePolicyKey,
    (_, _) => new Verbara.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithCircuitBreaker(threshold: 3, openDuration: TimeSpan.FromSeconds(60))
        .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(500))
        .WithTimeout(TimeSpan.FromSeconds(60))
        .Build());

// report.pdf-render: circuit 3/120s + retry 1/1s + timeout 30s.
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    Verbara.Platform.Api.Services.Reports.HttpPdfReportRenderer.ResiliencePolicyKey,
    (_, _) => new Verbara.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithCircuitBreaker(threshold: 3, openDuration: TimeSpan.FromSeconds(120))
        .WithRetry(maxAttempts: 1, baseDelay: TimeSpan.FromSeconds(1))
        .WithTimeout(TimeSpan.FromSeconds(30))
        .Build());

// storage.s3: circuit 5/60s + retry 3/500ms + timeout 30s. AWS SDK's built-in retry is
// disabled inside S3MediaStorage (MaxErrorRetry = 0) to avoid double-retry.
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    Verbara.Platform.Media.S3MediaStorage.ResiliencePolicyKey,
    (_, _) => new Verbara.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithCircuitBreaker(threshold: 5, openDuration: TimeSpan.FromSeconds(60))
        .WithRetry(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(500))
        .WithTimeout(TimeSpan.FromSeconds(30))
        .Build());

// ─── Pro.Routing — Skill Catalog (in-memory, singleton) ─────────────────────
builder.Services.AddSingleton<SkillCatalogBase>(new InMemorySkillCatalog());

// ─── Agent Assist Config Store (singleton for mutable admin config) ───────────
builder.Services.AddSingleton<AgentAssistConfigStore>();

// ─── System Settings Store (singleton for mutable system settings) ────────────
builder.Services.AddSingleton<SystemSettingsStore>();

// ─── Scheduled Reports + Email (via external microservices) ─────────────────
var serviceKey = builder.Configuration["Services:ServiceKey"] ?? "";
builder.Services.AddHttpClient("renderer", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Services:Renderer:BaseUrl"] ?? "http://renderer:5010");
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.Add("X-Service-Key", serviceKey);
});
builder.Services.AddHttpClient("mail", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Services:Mail:BaseUrl"] ?? "http://mail:5020");
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.Add("X-Service-Key", serviceKey);
});
// Shared transient-retry policy for the Mail microservice HTTP calls (v1.9.1 Frente A).
// HttpEmailService (send) and HttpEmailTemplateService (render) both consume this key
// since they target the same upstream "mail" service.
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    Verbara.Platform.Api.Services.HttpEmailService.ResiliencePolicyKey,
    (_, _) => new Verbara.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithCircuitBreaker(threshold: 5, openDuration: TimeSpan.FromSeconds(45))
        .WithRetry(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(300))
        .WithTimeout(TimeSpan.FromSeconds(10))
        .Build());
builder.Services.AddSingleton<Verbara.Platform.Core.Email.IEmailService,
    Verbara.Platform.Api.Services.HttpEmailService>();
builder.Services.AddSingleton<Verbara.Platform.Core.Email.IEmailTemplateService,
    Verbara.Platform.Api.Services.HttpEmailTemplateService>();
builder.Services.AddSingleton<Verbara.Platform.Api.Services.NotificationService>();
builder.Services.AddSingleton<Verbara.Platform.Core.Notifications.INotificationService>(
    sp => sp.GetRequiredService<Verbara.Platform.Api.Services.NotificationService>());
builder.Services.AddKeyedSingleton<Verbara.Platform.Core.Reports.IReportRenderer,
    Verbara.Platform.Api.Services.Reports.HttpPdfReportRenderer>("pdf");
builder.Services.AddKeyedSingleton<Verbara.Platform.Core.Reports.IReportRenderer,
    Verbara.Platform.Api.Services.Reports.CsvReportRenderer>("csv");
// IScheduledReportStore — Postgres when available, InMemory otherwise (registered below with storage)
builder.Services.AddSingleton<IReportDataBuilder, Verbara.Platform.Api.Services.Reports.AgentPerformanceReportBuilder>();
builder.Services.AddSingleton<IReportDataBuilder, Verbara.Platform.Api.Services.Reports.QueueAnalyticsReportBuilder>();
builder.Services.AddSingleton<IReportDataBuilder, Verbara.Platform.Api.Services.Reports.ConversationSummaryReportBuilder>();
builder.Services.AddSingleton<Verbara.Platform.Api.Services.Reports.ReportDataBuilderRegistry>();
builder.Services.AddSingleton<Verbara.Platform.Api.Services.Reports.ReportSchedulerService>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Verbara.Platform.Api.Services.Reports.ReportSchedulerService>());

// ─── Dunning ─────────────────────────────────────────────────────────────────
builder.Services.Configure<DunningConfig>(o =>
{
    var s = builder.Configuration.GetSection("Dunning");
    if (int.TryParse(s["WarningDays"], out var wd)) o.WarningDays = wd;
    if (int.TryParse(s["DegradedDays"], out var dd)) o.DegradedDays = dd;
    if (int.TryParse(s["SuspendedDays"], out var sd)) o.SuspendedDays = sd;
    if (int.TryParse(s["PendingDeletionDays"], out var pdd)) o.PendingDeletionDays = pdd;
    if (int.TryParse(s["CheckIntervalHours"], out var cih)) o.CheckIntervalHours = cih;
});
builder.Services.AddHostedService<DunningService>();

// ─── ACD Distribution ───────────────────────────────────────────────────────
builder.Services.Configure<DistributionOptions>(o =>
{
    var s = builder.Configuration.GetSection("Distribution");
    if (int.TryParse(s["PollIntervalMs"], out var pim)) o.PollIntervalMs = pim;
    if (int.TryParse(s["OfferTimeoutSeconds"], out var ots)) o.OfferTimeoutSeconds = ots;
    if (int.TryParse(s["DefaultQueueTimeoutSeconds"], out var dqts)) o.DefaultQueueTimeoutSeconds = dqts;
    if (int.TryParse(s["DefaultWrapUpTimeoutSeconds"], out var dwuts)) o.DefaultWrapUpTimeoutSeconds = dwuts;
    if (int.TryParse(s["MaxConversationsPerCycle"], out var mcpc)) o.MaxConversationsPerCycle = mcpc;
});
builder.Services.AddHostedService<QueueDistributionWorker>();
builder.Services.AddHostedService<ConversationTimeoutWorker>();

// ─── Verbara Capacity Sync (voice ↔ digital) ──────────────────────────────
builder.Services.AddHostedService<VerbaraCapacitySyncService>();

// ─── Outbound Webhooks ──────────────────────────────────────────────────────
builder.Services.Configure<Verbara.Platform.Core.Webhooks.CircuitBreakerOptions>(o =>
{
    var s = builder.Configuration.GetSection("Webhooks:CircuitBreaker");
    if (int.TryParse(s["FailureThreshold"], out var ft)) o.FailureThreshold = ft;
    if (int.TryParse(s["CooldownSeconds"], out var cs)) o.CooldownSeconds = cs;
    if (int.TryParse(s["MaxCooldownSeconds"], out var mcs)) o.MaxCooldownSeconds = mcs;
    if (double.TryParse(s["CooldownMultiplier"], System.Globalization.CultureInfo.InvariantCulture, out var cm)) o.CooldownMultiplier = cm;
});
builder.Services.AddSingleton<Verbara.Platform.Core.Webhooks.CircuitBreakerPolicy>();
builder.Services.AddSingleton<WebhookDispatcher>();
// Transient-retry policy for the HTTP send call inside WebhookDeliveryService.
// Orthogonal to the per-subscription CircuitBreakerPolicy above (which persists
// circuit state on the WebhookSubscription entity — a product feature).
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    WebhookDeliveryService.ResiliencePolicyKey,
    (_, _) => new Verbara.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithCircuitBreaker(threshold: 5, openDuration: TimeSpan.FromSeconds(30))
        .WithRetry(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(500))
        .WithTimeout(TimeSpan.FromSeconds(10))
        .Build());
builder.Services.AddHostedService<WebhookDeliveryService>();
builder.Services.AddHttpClient("webhooks");
builder.Services.AddHttpClient("EmailAttachments", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.MaxResponseContentBufferSize = 25 * 1024 * 1024;
});

// ─── Auth Services ──────────────────────────────────────────────────────────
// PasswordService and MfaService are static — no DI registration needed

// DataProtection must be registered before JwtTokenService so the factory can resolve it.
// Per ADR-0003 (R5.2 P0.8): DB-backed via PlatformDataProtectionDbContext. Fail-fast if
// Postgres connection unavailable in production environments. Test environments
// (WebApplicationFactory<Program>) fall back to ephemeral keys so endpoint tests
// don't require a live Postgres connection just to exercise non-encryption paths.
if (string.IsNullOrEmpty(coreConnectionString))
{
    if (!builder.Environment.IsEnvironment("Testing"))
        throw new InvalidOperationException(
            "ConnectionStrings:Postgres is required for DataProtection key persistence (ADR-0003) " +
            "in non-Testing environments. Configure the connection string or run under Environment=Testing.");

    builder.Services.AddPlatformDataProtection(opt =>
    {
        opt.ApplicationName = "Verbara.Platform.Testing";
        opt.UseEphemeralKeysForTesting();
    });
}
else
{
    builder.Services.AddDbContext<PlatformDataProtectionDbContext>(opt =>
        opt.UseNpgsql(coreConnectionString));
    builder.Services.AddPlatformDataProtection(opt =>
    {
        opt.ApplicationName = "Verbara.Platform";
        // Default: DB-backed via PlatformDataProtectionDbContext.
        // Override via opt.UseFileSystem("/path") for single-node deploys with mounted volume.
    });
}
builder.Services.AddSingleton<IJtiRevocationCache, InMemoryJtiRevocationCache>();

// AgentAssist runtime feature toggle (R5.1 Task J) — always registered so the admin
// endpoint surface resolves regardless of whether Pro.Analytics Postgres is wired.
// The toggle reports Enabled=false at startup (unless seeded via the "AgentAssist"
// config section) which causes the engine — when AddProAgentAssist runs below — to
// skip transcription at each session start.
// Credentials are persisted through AgentAssistCredentialsProtector (DataProtection
// purpose "AgentAssist.Credentials"); they never leave the API layer in plaintext.
// Multi-instance limitation: state lives per-node; each API node re-seeds from
// appsettings on restart. Postgres-backed variant tracked for R5.2.
builder.Services.Configure<Verbara.Platform.Core.Configuration.AgentAssistOptions>(
    builder.Configuration.GetSection("AgentAssist"));
builder.Services.AddSingleton<Verbara.Platform.Api.Services.AgentAssist.AgentAssistCredentialsProtector>();
builder.Services.AddSingleton<Verbara.Sdk.Pro.AgentAssist.Features.IAgentAssistFeatureToggle,
    Verbara.Platform.Api.Services.AgentAssist.InMemoryAgentAssistFeatureToggle>();

var jwtKeyDirectory = builder.Configuration["Auth:KeyDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");

// AHH Phase 3.D — JwtTokenService construction depends on whether the
// deployment opts into the multi-replica rotation pool.
//
//   Identity:JwtKeyRotation:UseRotationPool = true   → pool path
//                                              false  → file path (default)
//   Identity:JwtKeyRotation:RequireRedisStore = true  → fail-fast at startup
//                                              if ConnectionStrings:IdentityRedis is missing
//                                              (production multi-replica safety net)
//
// See docs/decisions/0012-jwt-rotation-pool-wireup-and-multi-replica-gate.md.
var useRotationPool = builder.Configuration.GetValue<bool>("Identity:JwtKeyRotation:UseRotationPool");
var requireRedisStore = builder.Configuration.GetValue<bool>("Identity:JwtKeyRotation:RequireRedisStore");

if (useRotationPool && requireRedisStore && string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("IdentityRedis")))
{
    throw new InvalidOperationException(
        "Identity:JwtKeyRotation:RequireRedisStore=true but ConnectionStrings:IdentityRedis is not set. " +
        "Multi-replica deployments require a Redis-backed IJwtKeyStore so the JWT signing key is shared across replicas; " +
        "without it, tokens issued by one replica are rejected by every other replica. " +
        "Set ConnectionStrings:IdentityRedis OR set RequireRedisStore=false to acknowledge single-replica operation.");
}

if (useRotationPool)
{
    builder.Services.AddSingleton<JwtTokenService>(sp => new JwtTokenService(
        sp.GetRequiredService<Verbara.Platform.Identity.Auth.Jwt.IJwtKeyRotationService>(),
        sp.GetRequiredService<IJtiRevocationCache>()));

    // Idempotent legacy-file → rotation-pool migration. Runs once at startup
    // before the first request so already-issued (file-RSA-signed) tokens
    // continue validating after the cutover.
    builder.Services.AddHostedService(sp => new JwtLegacyKeyMigrationService(
        sp.GetRequiredService<Verbara.Platform.Identity.Auth.Jwt.IJwtKeyStore>(),
        sp.GetRequiredService<IDataProtectionProvider>(),
        jwtKeyDirectory,
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JwtLegacyKeyMigrationService>>()));
}
else
{
    // Single-replica file-based path (R5.4 + earlier behavior).
    builder.Services.AddSingleton<JwtTokenService>(sp => new JwtTokenService(
        jwtKeyDirectory,
        sp.GetRequiredService<IDataProtectionProvider>(),
        sp.GetRequiredService<IJtiRevocationCache>()));
}

// R5.4 S5.9 — JWT signing-key rotation pool. The store defaults to in-memory
// (single-process safe) and is replaced by RedisJwtKeyStore via
// AddVerbaraPlatformIdentityRedis when a clustered deploy registers
// IConnectionMultiplexer + RedisIdentityOptions. Bound options come from the
// "Identity:JwtKeyRotation" config section (KeySizeBytes / ActiveDuration /
// GracePeriod). The active-key consumer surface is the rotation service +
// the /management/security/jwt/* admin endpoints; JwtTokenService continues
// to use its RSA key file for issuance until R6 swaps to symmetric keys
// pulled from this pool.
builder.Services.Configure<Verbara.Platform.Identity.Auth.Jwt.JwtKeyRotationOptions>(
    builder.Configuration.GetSection("Identity:JwtKeyRotation"));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.TryAddSingleton<Verbara.Platform.Identity.Auth.Jwt.IJwtKeyStore,
    Verbara.Platform.Identity.Auth.Jwt.InMemoryJwtKeyStore>();
builder.Services.AddSingleton<Verbara.Platform.Identity.Auth.Jwt.IJwtKeyRotationService,
    Verbara.Platform.Identity.Auth.Jwt.JwtKeyRotationService>();

builder.Services.AddSingleton<RefreshTokenService>();
builder.Services.AddSingleton<AuthEventService>();
builder.Services.AddSingleton<AccountLockoutService>();
builder.Services.AddSingleton<SessionService>();
// R5.2 PA.1 — MFA admin service backing /management/mfa endpoints.
builder.Services.AddSingleton<
    Verbara.Platform.Api.Endpoints.Mfa.IMfaAdminService,
    Verbara.Platform.Api.Endpoints.Mfa.MfaAdminService>();

// R5.2 PB.1 — audit log viewer query service backing /admin/audit/events
// + /admin/audit/export. Wraps IAuditStore with the presentation-layer DTO
// so the React DataTable can render rows directly without re-shaping.
builder.Services.AddSingleton<
    Verbara.Platform.Api.Endpoints.Audit.IAuditQueryService,
    Verbara.Platform.Api.Endpoints.Audit.DefaultAuditQueryService>();

// R5.2 PB.2 + C.7 — admin impersonation session store + auto-timeout sweep.
// Default in-memory store (single-process safe); multi-instance Platform
// deployments swap this for a Redis or Postgres-backed store via override.
builder.Services.AddSingleton<
    Verbara.Platform.Core.Impersonation.IImpersonationSessionStore,
    Verbara.Platform.Core.Impersonation.InMemoryImpersonationSessionStore>();
builder.Services.AddHostedService<
    Verbara.Platform.Api.Services.ImpersonationSessionTimeoutService>();
// TimeProvider is required by the impersonation endpoints + the timeout
// sweep; register the system clock if no test-time replacement was injected.
if (!builder.Services.Any(d => d.ServiceType == typeof(TimeProvider)))
    builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TenantProvisioningService>();
builder.Services.AddSingleton<ITenantLifecycleHandler>(sp => sp.GetRequiredService<TenantProvisioningService>());

// ─── MFA Policy Evaluator ────────────────────────────────────────────────────
builder.Services.AddSingleton<Verbara.Platform.Identity.Mfa.IMfaPolicyEvaluator,
    Verbara.Platform.Identity.Mfa.TenantAuthConfigMfaPolicyEvaluator>();

// R5.2 PA.2 — Recovery code generation/hashing for profile-scoped MFA wizard.
builder.Services.AddSingleton<Verbara.Platform.Identity.Mfa.IRecoveryCodeService,
    Verbara.Platform.Identity.Mfa.RecoveryCodeService>();

// ─── MFA / Password-Reset Token Caches ─────────────────────────────────────
// Default: in-memory implementations (single-instance safe). When
// ConnectionStrings:IdentityRedis is set, AddVerbaraPlatformIdentityRedis
// replaces both registrations with Redis-backed impls so MFA challenge +
// password-reset tokens survive failover across multiple API instances.
builder.Services.AddSingleton<Verbara.Platform.Identity.Mfa.IMfaPendingCache,
    Verbara.Platform.Identity.Mfa.InMemoryMfaPendingCache>();
builder.Services.AddSingleton<Verbara.Platform.Identity.Mfa.IPasswordResetCache,
    Verbara.Platform.Identity.Mfa.InMemoryPasswordResetCache>();

var identityRedisConn = builder.Configuration.GetConnectionString("IdentityRedis");

// v1.14.4 (MFA-007 fix) — multi-replica deployments MUST set both
// `Identity:RequireRedisIdentityCaches=true` and a non-empty
// `ConnectionStrings:IdentityRedis`. Without Redis-backed
// IMfaPendingCache + IPasswordResetCache + IJtiRevocationCache, an MFA
// challenge issued by replica A is invisible to replica B (the user
// gets 401 on submission); a password-reset email link clicked through
// to a different replica fails identically. The fail-fast guard makes
// this misconfiguration surface at startup instead of as silent runtime
// 401s. The flag defaults to false to preserve single-replica behavior.
//
// Note: AHH already has `Identity:JwtKeyRotation:RequireRedisStore` for
// the JWT key path; this companion flag covers MFA + password-reset +
// JTI revocation caches that are independent of the rotation pool.
var requireRedisIdentityCaches =
    builder.Configuration.GetValue<bool>("Identity:RequireRedisIdentityCaches");
if (requireRedisIdentityCaches && string.IsNullOrWhiteSpace(identityRedisConn))
{
    throw new InvalidOperationException(
        "Identity:RequireRedisIdentityCaches=true but ConnectionStrings:IdentityRedis is not set. " +
        "Multi-replica deployments require Redis-backed IMfaPendingCache + IPasswordResetCache + " +
        "IJtiRevocationCache so MFA challenges and password-reset tokens issued on one replica " +
        "remain valid when the user's next request lands on a different replica. " +
        "Set ConnectionStrings:IdentityRedis OR set RequireRedisIdentityCaches=false to acknowledge " +
        "single-replica operation.");
}

if (!string.IsNullOrWhiteSpace(identityRedisConn))
{
    builder.Services.AddVerbaraPlatformIdentityRedis(o =>
    {
        o.ConnectionString = identityRedisConn;
        var prefix = builder.Configuration["Identity:Redis:KeyPrefix"];
        if (!string.IsNullOrWhiteSpace(prefix))
            o.KeyPrefix = prefix;
    });
    // AHH Phase 1: cluster-wide cache invalidation via Redis pubsub.
    // Engages only when Redis is configured; in single-instance deploys the
    // cache decorators rely on local TTL only (60 s default).
    builder.Services.AddAuthHotpathRedisInvalidation();
}

// ─── OIDC SSO Services ──────────────────────────────────────────────────────
builder.Services.AddHttpClient("oidc");
// Transient-retry policy for OIDC token-exchange POST — retry 2 attempts (500ms base),
// 10s per-attempt timeout, circuit opens after 3 consecutive failures for 120s.
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    OidcTokenExchangeService.ResiliencePolicyKey,
    (_, _) => new Verbara.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithCircuitBreaker(threshold: 3, openDuration: TimeSpan.FromSeconds(120))
        .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(500))
        .WithTimeout(TimeSpan.FromSeconds(10))
        .Build());
builder.Services.AddSingleton<IOidcTokenExchangeService, OidcTokenExchangeService>();
builder.Services.AddSingleton<IOidcUserProvisioningService, OidcUserProvisioningService>();

// ─── Per-worker resilience policies (v1.9.1 Frente C — 12 BackgroundServices) ────
// Each keyed policy wraps a single tick of its owning BackgroundService. Circuit-open
// causes the worker to skip the current tick and retry on the next scheduled tick;
// the outer `while` loop is NOT wrapped.
//
// Shared default budget: circuit 5/60s + retry 2/500ms + timeout 10s.
// Tighter DB-heavy budget for retention/audit/bot-analytics: circuit 3/120s + retry
// 1/2s + timeout 20s (allow long-running batch DELETEs).
// Tighter hourly-cadence budget for dunning: circuit 3/600s + retry 1/5s + timeout 60s.
static Verbara.Sdk.Resilience.ResiliencePolicy BuildDefaultWorkerPolicy() =>
    new Verbara.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithCircuitBreaker(threshold: 5, openDuration: TimeSpan.FromSeconds(60))
        .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(500))
        .WithTimeout(TimeSpan.FromSeconds(10))
        .Build();
static Verbara.Sdk.Resilience.ResiliencePolicy BuildDbHeavyWorkerPolicy() =>
    new Verbara.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithCircuitBreaker(threshold: 3, openDuration: TimeSpan.FromSeconds(120))
        .WithRetry(maxAttempts: 1, baseDelay: TimeSpan.FromSeconds(2))
        .WithTimeout(TimeSpan.FromSeconds(20))
        .Build();
static Verbara.Sdk.Resilience.ResiliencePolicy BuildHourlyWorkerPolicy() =>
    new Verbara.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithCircuitBreaker(threshold: 3, openDuration: TimeSpan.FromSeconds(600))
        .WithRetry(maxAttempts: 1, baseDelay: TimeSpan.FromSeconds(5))
        .WithTimeout(TimeSpan.FromSeconds(60))
        .Build();

// Default-budget workers
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    ConversationTimeoutWorker.ResiliencePolicyKey, (_, _) => BuildDefaultWorkerPolicy());
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    QueueDistributionWorker.ResiliencePolicyKey, (_, _) => BuildDefaultWorkerPolicy());
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    Verbara.Platform.Api.Services.Reports.ReportSchedulerService.ResiliencePolicyKey,
    (_, _) => BuildDefaultWorkerPolicy());
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    VerbaraCapacitySyncService.ResiliencePolicyKey, (_, _) => BuildDefaultWorkerPolicy());
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    RealtimeStateBridge.ResiliencePolicyKey, (_, _) => BuildDefaultWorkerPolicy());
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    CampaignMetricsPoller.ResiliencePolicyKey, (_, _) => BuildDefaultWorkerPolicy());
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    AgentAssistBridge.ResiliencePolicyKey, (_, _) => BuildDefaultWorkerPolicy());
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    Verbara.Platform.Automation.TimerPollingService.ResiliencePolicyKey,
    (_, _) => BuildDefaultWorkerPolicy());

// DB-heavy workers (long batch DELETEs / bulk inserts)
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    RetentionPurgeService.ResiliencePolicyKey, (_, _) => BuildDbHeavyWorkerPolicy());
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    AuditRetentionService.ResiliencePolicyKey, (_, _) => BuildDbHeavyWorkerPolicy());
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    BotAnalyticsPersistenceService.ResiliencePolicyKey, (_, _) => BuildDbHeavyWorkerPolicy());

// Hourly-cadence worker
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    Verbara.Platform.Billing.DunningService.ResiliencePolicyKey,
    (_, _) => BuildHourlyWorkerPolicy());

// ─── Pro.Dialer (Outbound Campaigns) ────────────────────────────────────────
var dialerConnectionString = ConnectionStringDefaults.ApplyPoolDefaults(
    builder.Configuration.GetConnectionString("Dialer") ?? builder.Configuration.GetConnectionString("Postgres")) ?? "";
if (!string.IsNullOrEmpty(dialerConnectionString))
{
    builder.Services.UsePostgresDialerStorage(ResolveDataSource(dialerConnectionString)!);
    builder.Services.AddProDialer(o => { });
    builder.Services.AddDialerRetentionTargets();
    builder.Services.AddHostedService<CampaignMetricsPoller>();
}

// ─── Pro.Cluster ─────────────────────────────────────────────────────────────
builder.Services.AddVerbaraCluster(c =>
{
    c.InstanceId = Environment.MachineName;

    // Register the primary Asterisk server as initial cluster node
    var amiSection = builder.Configuration.GetSection("Asterisk:Ami");
    var ariSection = builder.Configuration.GetSection("Asterisk:Ari");
    var amiHost = amiSection["Hostname"];
    if (!string.IsNullOrEmpty(amiHost))
    {
        var nodeOptions = new ClusterNodeOptions
        {
            Ami = new AmiConnectionOptions
            {
                Hostname = amiHost,
                Port = int.Parse(amiSection["Port"] ?? "5038", System.Globalization.CultureInfo.InvariantCulture),
                Username = amiSection["Username"] ?? "admin",
                Password = amiSection["Password"] ?? "",
                UseSsl = bool.Parse(amiSection["UseSsl"] ?? "false"),
            },
        };

        // ARI is optional — only configured if section exists
        var ariBaseUrl = ariSection["BaseUrl"];
        if (!string.IsNullOrEmpty(ariBaseUrl))
        {
            nodeOptions.Ari = new Verbara.Sdk.Ari.Client.AriClientOptions
            {
                BaseUrl = ariBaseUrl,
                Username = ariSection["Username"] ?? "",
                Password = ariSection["Password"] ?? "",
                Application = ariSection["Application"] ?? "verbara-platform",
            };
        }

        c.InitialNodes["primary"] = nodeOptions;
    }
});
var clusterConn = ConnectionStringDefaults.ApplyPoolDefaults(
    builder.Configuration.GetConnectionString("Cluster")
        ?? builder.Configuration.GetConnectionString("Postgres"));
if (!string.IsNullOrEmpty(clusterConn))
{
    builder.Services.UsePostgresClusterTransport(ResolveDataSource(clusterConn)!);
}

// ─── Pro.MultiTenant ─────────────────────────────────────────────────────────
builder.Services.AddVerbaraMultiTenant();

// ─── Pro.Realtime (replaces AsteriskRealtimeSyncService) ─────────────────────
builder.Services.AddVerbaraRealtime(o =>
{
    o.ReconcilerIntervalSeconds = 60;
    o.EnableAgentPresenceTracking = false;
});
var realtimeConn = ConnectionStringDefaults.ApplyPoolDefaults(
    builder.Configuration.GetConnectionString("Realtime")
        ?? builder.Configuration.GetConnectionString("Analytics")
        ?? builder.Configuration.GetConnectionString("Postgres")) ?? "";
if (!string.IsNullOrEmpty(realtimeConn))
    builder.Services.UsePostgresRealtimeStorage(ResolveDataSource(realtimeConn)!);
builder.Services.AddHostedService<RealtimeStateBridge>();

// Queue membership service + desired state provider for reconciler
builder.Services.AddSingleton<QueueMembershipService>();
builder.Services.AddSingleton<IDesiredStateProvider, PlatformDesiredStateProvider>();

// Queue-member pause tracker (ephemeral, per-instance) — backs the IsPaused
// projection exposed by QueueMembersEndpoints. Authoritative pause state
// lives in Asterisk Realtime; this is a UI-facing hint only.
builder.Services.AddSingleton<QueueMemberPauseTracker>();

// Trunk decorator — wraps PostgresTrunkStore with Realtime sync (only when Dialer is configured)
if (!string.IsNullOrEmpty(dialerConnectionString))
{
    builder.Services.AddSingleton<TrunkStoreBase>(sp =>
        new RealtimeSyncingTrunkStore(
            new PostgresTrunkStore(sp.GetRequiredService<DialerDbContext>()),
            sp.GetRequiredService<IRealtimeSyncService>()));
}
else
{
    builder.Services.AddSingleton<TrunkStoreBase>(sp =>
        new InMemoryTrunkStore());
}

// ─── Pro EventStore + Analytics + CallAnalytics (engines + Postgres stores) ──
var analyticsConnectionString = ConnectionStringDefaults.ApplyPoolDefaults(
    builder.Configuration.GetConnectionString("Analytics")) ?? dialerConnectionString;
if (!string.IsNullOrEmpty(analyticsConnectionString))
{
    var analyticsDataSource = ResolveDataSource(analyticsConnectionString)!;
    builder.Services.UsePostgresEventStore(analyticsDataSource);
    builder.Services.AddProCallAnalyticsPostgres(analyticsDataSource);
    builder.Services.UsePostgresAnalyticsStore(analyticsDataSource);

    // Pro engine registrations — require ICallSessionManager (wired by AddVerbaraSessionsMultiServer)
    builder.Services.AddVerbaraEventStore();
    // ADR-0002 / ADR-0004 §"Pro.Analytics": single-tenant deploys must opt in
    // explicitly. Without WithSingleTenantMode the engine has no DefaultTenantId
    // and LiveQueueSnapshotWriter rejects events with reason=missing_tenant.
    builder.Services.AddVerbaraAnalytics().WithSingleTenantMode("default");
    builder.Services.AddProCallAnalytics();
    builder.Services.AddProAgentAssist(assistBuilder =>
    {
        assistBuilder.WithRuntimeFeatureToggle(sp =>
            sp.GetRequiredService<Verbara.Sdk.Pro.AgentAssist.Features.IAgentAssistFeatureToggle>());
    });

    // Pro.Analytics.Live (v1.12.0-pro) — LiveQueueSnapshotWriter hosted service +
    // Postgres-backed ILiveQueueMetricsProvider for QueueMetricsEndpoints. Uses
    // the dedicated "AnalyticsLive" connection string when provided (matches
    // project convention: ConnectionStrings__AnalyticsLive env var or nested
    // appsettings "ConnectionStrings:AnalyticsLive"), else falls back to the
    // shared Analytics connection string (same DB).
    // KNOWN LIMITATION (R5.1 Task H): writer emits tenant_id="" because Platform
    // registers Pro.Analytics as process-scope singleton with empty DefaultTenantId.
    // Per-tenant scope refactor is tracked for R5.2 / future Platform patch.
    var liveAnalyticsConnectionString = ConnectionStringDefaults.ApplyPoolDefaults(
        builder.Configuration.GetConnectionString("AnalyticsLive")) ?? analyticsConnectionString;
    builder.Services.AddVerbaraProAnalyticsLive();
    // Reuse analyticsDataSource when AnalyticsLive shares the conn string
    // (typical), else build a dedicated DataSource for the distinct string.
    var liveAnalyticsDataSource = string.Equals(
        liveAnalyticsConnectionString, analyticsConnectionString, StringComparison.Ordinal)
            ? analyticsDataSource
            : ResolveDataSource(liveAnalyticsConnectionString)!;
    builder.Services.UsePostgresProAnalyticsLive(liveAnalyticsDataSource);

    // AgentAssist Postgres query stores (read-only endpoints for supervisor dashboard).
    // Shares analyticsDataSource per ADR-0015 Phase 2.
    builder.Services.AddProAgentAssistPostgres(analyticsDataSource);

    // Pro Retention targets (v1.8.0-pro) — DryRun=true default via AddProRetention
    builder.Services.AddEventStoreRetentionTargets();
    builder.Services.AddAnalyticsRetentionTargets();
    builder.Services.AddCallAnalyticsRetentionTargets();
    builder.Services.AddAgentAssistRetentionTargets();
}

// ─── Pro Analytics InMemory fallbacks (when no Analytics/Dialer connection string) ──
if (!builder.Services.Any(d => d.ServiceType == typeof(Verbara.Sdk.Pro.EventStore.ICompletedSessionStore)))
    builder.Services.AddSingleton<Verbara.Sdk.Pro.EventStore.ICompletedSessionStore, Verbara.Platform.Storage.InMemory.InMemoryCompletedSessionStore>();
if (!builder.Services.Any(d => d.ServiceType == typeof(Verbara.Sdk.Pro.Analytics.IIntervalSnapshotStore)))
    builder.Services.AddSingleton<Verbara.Sdk.Pro.Analytics.IIntervalSnapshotStore, Verbara.Platform.Storage.InMemory.InMemoryIntervalSnapshotStore>();
if (!builder.Services.Any(d => d.ServiceType == typeof(Verbara.Sdk.Pro.CallAnalytics.Store.ICallAnalyticsStore)))
    builder.Services.AddSingleton<Verbara.Sdk.Pro.CallAnalytics.Store.ICallAnalyticsStore, Verbara.Platform.Storage.InMemory.InMemoryCallAnalyticsStore>();

// ─── Recordings ─────────────────────────────────────────────────────────────
builder.Services.Configure<RecordingOptions>(o =>
{
    var path = builder.Configuration["Recordings:BasePath"];
    if (!string.IsNullOrEmpty(path))
        o.BasePath = path;
});

// ─── S3 Media Storage (overrides FileSystem default when S3_BUCKET is set) ──
var s3Bucket = builder.Configuration["S3_BUCKET"];
if (!string.IsNullOrEmpty(s3Bucket))
{
    var s3Endpoint = builder.Configuration["S3_ENDPOINT"] ?? "https://s3.amazonaws.com";
    var s3Region   = builder.Configuration["S3_REGION"];
    var s3ForcePathStyle = !string.Equals(
        builder.Configuration["S3_FORCE_PATH_STYLE"], "false",
        StringComparison.OrdinalIgnoreCase);

    // Replace the FileSystemMediaStorage registered by AddPlatformMedia()
    builder.Services.AddSingleton<IMediaStorage>(sp =>
    {
        var policy = sp.GetKeyedService<Verbara.Sdk.Resilience.ResiliencePolicy>(
            Verbara.Platform.Media.S3MediaStorage.ResiliencePolicyKey);
        return new S3MediaStorage(s3Bucket, s3Endpoint, s3Region, s3ForcePathStyle, policy);
    });
}

// ─── Authentication (JWT + API key dual-scheme) ──────────────────────────────

builder.Services.AddDynamicAuth();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
    options.AddPolicy("SupervisorPlus", p => p.RequireRole("Admin", "Supervisor"));
    options.AddPolicy("Authenticated", p => p.RequireAuthenticatedUser());
    options.AddPolicy("PlatformAdminOnly", p =>
        p.AddRequirements(new PlatformAdminRequirement()));
    options.AddPolicy("PartnerAdminOnly", p =>
        p.AddRequirements(new PartnerAdminRequirement()));

    // R5.2 PA.1 — MFA admin surface. PlatformAdminRequirement combines the
    // host/partner-tenant gate with the seeded "security.mfa.admin" RBAC
    // permission so the surface is double-locked: only platform admins (or
    // partner admins managing their children) with the explicit permission can
    // list users / reset MFA / revoke sessions.
    options.AddPolicy(
        Verbara.Platform.Api.Endpoints.Mfa.MfaAdminEndpoints.AuthorizationPolicy,
        p => p.AddRequirements(new PlatformAdminRequirement("security.mfa.admin")));

    // R5.2 PB.1 — audit log viewer + export. Two policies so the export surface
    // can be revoked independently of read access (compliance scenarios where
    // viewing in-app is fine but mass extract requires extra approval). Both
    // run through PlatformAdminRequirement (host/partner gate + seeded
    // dot-notation permission `audit.read` / `audit.export`).
    options.AddPolicy(
        Verbara.Platform.Api.Endpoints.Audit.AuditAdminEndpoints.QueryPolicy,
        p => p.AddRequirements(new PlatformAdminRequirement("audit.read")));
    options.AddPolicy(
        Verbara.Platform.Api.Endpoints.Audit.AuditAdminEndpoints.ExportPolicy,
        p => p.AddRequirements(new PlatformAdminRequirement("audit.export")));

    // R5.2 PB.2 — impersonation admin surface. Same double-lock pattern as
    // PA.1 / PB.1: PlatformAdminRequirement combines host/partner-tenant
    // gating with the seeded `security.impersonation.manage` permission so
    // only platform admins (or partner admins managing their children) with
    // the explicit permission can list / revoke active sessions or read
    // history.
    options.AddPolicy(
        Verbara.Platform.Api.Endpoints.ManagementImpersonationEndpoints.AdminAuthorizationPolicy,
        p => p.AddRequirements(new PlatformAdminRequirement("security.impersonation.manage")));

    // R5.2 PC.1 — retention admin surface. Same double-lock pattern as
    // PA.1 / PB.1 / PB.2: PlatformAdminRequirement combines host/partner
    // tenant gating with the seeded `retention.read` (overview) /
    // `retention.manage` (DryRun toggle + manual run-now) permissions
    // (P0.9 commit f20892e).
    options.AddPolicy(
        Verbara.Platform.Api.Endpoints.Retention.RetentionAdminEndpoints.ReadPolicy,
        p => p.AddRequirements(new PlatformAdminRequirement("retention.read")));
    options.AddPolicy(
        Verbara.Platform.Api.Endpoints.Retention.RetentionAdminEndpoints.ManagePolicy,
        p => p.AddRequirements(new PlatformAdminRequirement("retention.manage")));

    // R5.4 S5.9 — JWT signing-key rotation surface. Same double-lock pattern
    // as the R5.2 admin surfaces: PlatformAdminRequirement combines host/partner
    // tenant gating with the seeded `security.jwt.rotate` permission. Closes
    // C.1 of post-R5.1 triage (v1.9.2 partial single-key impl).
    options.AddPolicy(
        Verbara.Platform.Api.Endpoints.Security.JwtKeyEndpoints.AuthorizationPolicy,
        p => p.AddRequirements(new PlatformAdminRequirement("security.jwt.rotate")));

    // Plan 32C — PlatformHub method-level policies. "Supervisor" is role-based
    // (Supervisor or Admin); "Agent" is role-based for UpdatePresence/RequestHelp;
    // "PlatformAdmin" is role-based for hub-level administrative methods (distinct
    // from the existing "PlatformAdminOnly" which uses the PlatformAdminRequirement).
    options.AddPolicy("Supervisor", p => p.RequireRole("Supervisor", "Admin"));
    options.AddPolicy("Agent", p => p.RequireRole("Agent", "Supervisor", "Admin"));
    options.AddPolicy("PlatformAdmin", p => p.RequireRole("PlatformAdmin"));
});

// RBAC permission-based authorization
builder.Services.AddSingleton<PermissionResolver>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, PartnerAdminAuthorizationHandler>();

// ─── Health Checks ───────────────────────────────────────────────────────────

builder.Services.AddSingleton<Verbara.Platform.Api.Health.IServiceHeartbeat, Verbara.Platform.Api.Health.ServiceHeartbeat>();

// Frente D (v1.9.1): resilience-state-aware health checks. The observer listens
// to the Verbara.Sdk.Resilience meter's circuit.state observable gauge and
// caches per-policy-key state + timestamp so HealthCheck implementations can
// distinguish Healthy / Degraded / Unhealthy based on how long a circuit has
// been open.
builder.Services.Configure<Verbara.Platform.Api.Health.PlatformHealthCheckOptions>(_ => { });
builder.Services.AddSingleton<Verbara.Platform.Api.Health.ResilienceStateObserver>();
builder.Services.AddSingleton<Verbara.Platform.Api.Health.IResilienceStateObserver>(
    sp => sp.GetRequiredService<Verbara.Platform.Api.Health.ResilienceStateObserver>());
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<Verbara.Platform.Api.Health.ResilienceStateObserver>());

// Dedicated HealthCheck-owned resilience policy: 2s timeout, no retry, no
// circuit (we want every probe to run). Surfaces Postgres-under-load as
// Unhealthy with a clear reason instead of hanging on the outer HealthCheck
// timeout.
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    Verbara.Platform.Api.Health.PostgresHealthCheck.ResiliencePolicyKey,
    (_, _) => new Verbara.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithTimeout(TimeSpan.FromSeconds(2))
        .Build());

// R5.3 A.5.b — promote 4 Pro BackgroundServices to concrete singletons so the
// IHealthCheck implementations on those classes (added by Pro task A.5,
// Pro 1.13.0-pro commit 05adeb8) can be resolved by the health-check
// registry. Pro DI extensions register the services as
// AddHostedService<T> only (binding T -> IHostedService directly, no
// concrete singleton); without this swap, AddCheck<T>() below would resolve
// a *second* instance whose heartbeat never ticks and would always report
// Unhealthy. The pattern mirrors AnalyticsLiveServiceCollectionExtensions
// (R5.1 LiveQueueSnapshotWriter): register T as Singleton, then forward
// IHostedService via factory so both resolve to the same instance.
builder.Services
    .PromoteHostedServiceToSingleton<PresenceHeartbeatService>()
    .PromoteHostedServiceToSingleton<PresenceFanoutService>()
    .PromoteHostedServiceToSingleton<PresenceMergeConsumer>()
    .PromoteHostedServiceToSingleton<RetentionService>();

var healthBuilder = builder.Services.AddHealthChecks()
    .AddCheck<Verbara.Platform.Api.Health.BackgroundServiceHealthCheck>("services", tags: ["ready"])
    .AddCheck<Verbara.Platform.Api.Health.AsteriskAmiHealthCheck>("asterisk", tags: ["ready"])
    // R5.3 A.5.b — Pro 1.13.0-pro health checks (presence + retention).
    .AddCheck<PresenceHeartbeatService>("presence-heartbeat", tags: ["ready"])
    .AddCheck<PresenceFanoutService>("presence-fanout", tags: ["ready"])
    .AddCheck<PresenceMergeConsumer>("presence-merge", tags: ["ready"])
    .AddCheck<RetentionService>("retention", tags: ["ready"]);

// Only add Postgres health check if NpgsqlDataSource is registered
if (builder.Services.Any(d => d.ServiceType == typeof(NpgsqlDataSource)))
    healthBuilder.AddCheck<Verbara.Platform.Api.Health.PostgresHealthCheck>("postgres", tags: ["ready"]);

// ─── Rate Limiting ────────────────────────────────────────────────────────────

builder.Services.AddRateLimiter(TenantRateLimitPolicy.ConfigureRateLimiting);
builder.Services.AddSingleton<Verbara.Platform.Api.Services.TenantTierCache>();
builder.Services.AddSingleton<Verbara.Platform.Api.Services.FeatureGateCache>();
builder.Services.AddSingleton<Verbara.Platform.Core.IFeatureGateService, Verbara.Platform.Api.Services.DefaultFeatureGateService>();

// ─── API Versioning ───────────────────────────────────────────────────────────

builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = ApiVersionReader.Combine(
        new UrlSegmentApiVersionReader(),
        new HeaderApiVersionReader("X-Api-Version"));
});

// ─── CORS ─────────────────────────────────────────────────────────────────────

var corsOrigins = builder.Configuration["CORS_ORIGINS"]?.Split(',') ?? new[] { "*" };
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p
        .WithOrigins(corsOrigins)
        .AllowAnyMethod()
        .AllowAnyHeader()));

// ─── HTTP / Minimal API ───────────────────────────────────────────────────────

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
#pragma warning disable IL3050 // Non-generic JsonStringEnumConverter: fallback for enums not in ApiJsonContext
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
#pragma warning restore IL3050
});
// ─── OpenAPI (R5.3 Phase B Task B.7 / D.1) ───────────────────────────────────
//
// Spec generation + Scalar UI is opt-in outside Development to avoid leaking
// surface in production. Set `Platform__OpenApi__Enabled=true` (env var) or
// `Platform:OpenApi:Enabled=true` in configuration to enable in Production.
var openApiEnabled = builder.Environment.IsDevelopment()
    || builder.Configuration.GetValue<bool>("Platform:OpenApi:Enabled");

if (openApiEnabled)
{
    builder.Services.AddOpenApi();
}

var app = builder.Build();

// ─── Production Config Validation ────────────────────────────────────────────

if (app.Environment.IsProduction())
{
    var configErrors = new List<string>();

    if (string.IsNullOrEmpty(serviceKey) || serviceKey == "platform_internal_secret")
        configErrors.Add("Services:ServiceKey must be configured (not default) in production");

    if (corsOrigins.Contains("*"))
        configErrors.Add("CORS_ORIGINS must not contain '*' in production");

    var amiHost = builder.Configuration["Asterisk:Ami:Hostname"];
    if (!string.IsNullOrEmpty(amiHost))
    {
        var amiUser = builder.Configuration["Asterisk:Ami:Username"];
        var amiPass = builder.Configuration["Asterisk:Ami:Password"];
        if (string.IsNullOrEmpty(amiUser) || string.IsNullOrEmpty(amiPass))
            configErrors.Add("Asterisk:Ami:Username and Password are required when Ami:Hostname is set");
        // v1.14.4 (CFG-003 fix) — explicitly reject the appsettings.Development.json
        // dev values (`admin`/`admin`) so a misconfigured production deploy
        // doesn't silently inherit them via the development-profile fallback.
        else if (string.Equals(amiUser, "admin", StringComparison.Ordinal)
                 && string.Equals(amiPass, "admin", StringComparison.Ordinal))
            configErrors.Add(
                "Asterisk:Ami credentials match the appsettings.Development.json " +
                "fallback (admin/admin) — set via env var or user-secrets before " +
                "deploying to production");
    }

    if (configErrors.Count > 0)
        throw new InvalidOperationException(
            $"Production configuration errors:\n  - {string.Join("\n  - ", configErrors)}");
}

// ─── Middleware pipeline ──────────────────────────────────────────────────────

// Trust X-Forwarded-For from configured upstream proxies. Default empty →
// no header trust → RemoteIpAddress is the raw socket peer (no behaviour
// change for existing single-node deploys). See spec §4.5.
{
    var trustedProxiesSection = builder.Configuration.GetSection("ForwardedHeaders:TrustedProxies");
    var trustedProxies = trustedProxiesSection.GetChildren()
        .Select(c => c.Value)
        .Where(v => !string.IsNullOrEmpty(v))
        .Cast<string>()
        .ToArray();
    if (trustedProxies.Length > 0)
    {
        var fwdOptions = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                              | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
        };
        fwdOptions.KnownIPNetworks.Clear();
        fwdOptions.KnownProxies.Clear();
        foreach (var cidr in trustedProxies)
        {
            fwdOptions.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
        }
        app.UseForwardedHeaders(fwdOptions);
    }
}

app.UseWebSockets();
app.UseStaticFiles();
app.UseMiddleware<VersionRedirectMiddleware>();
app.UseRouting();
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseMiddleware<RateLimitHeadersMiddleware>();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
// MT-001 (PREPUB-2026-05-09): rejects header/subdomain-driven tenant overrides
// when the principal's tid claim does not match. Must run AFTER UseAuthorization
// so context.User is populated, BEFORE per-handler tenant trust kicks in.
app.UseMiddleware<TenantBoundaryValidationMiddleware>();
app.UseMiddleware<TenantStatusMiddleware>();
app.UseMiddleware<LicenseGateMiddleware>();
app.UseMiddleware<IpAllowlistMiddleware>();

if (openApiEnabled)
{
    // /openapi/v1.json — raw OpenAPI 3.0 spec.
    app.MapOpenApi();
    // /scalar/v1 — Scalar UI rendering the spec.
    app.MapScalarApiReference();
}
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
    ResponseWriter = Verbara.Platform.Api.Health.HealthReportJsonWriter.WriteAsync,
});
// Prometheus scraping endpoint at /metrics — exposes the full SDK/Pro meter
// catalog (resilience, licensing, cluster, push, etc.) registered via
// AddVerbaraOpenTelemetry(...).WithPrometheusExporter() above.
app.MapPrometheusScrapingEndpoint();

// ─── Versioned route group ───────────────────────────────────────────────────

var versionSet = app.NewApiVersionSet()
    .HasApiVersion(new ApiVersion(1, 0))
    .ReportApiVersions()
    .Build();

var v1 = app.MapGroup("/api/v{version:apiVersion}")
    .WithApiVersionSet(versionSet);

// ─── Endpoint mapping ────────────────────────────────────────────────────────

v1.MapAuthEndpoints();
// R5.2 PA.2 — Profile-scoped end-user MFA wizard + sessions + recovery-codes
// regenerate routes. Distinct from the legacy /auth/mfa/* surface so the
// dedicated wizard pages can hit stable URLs without coupling to login flow.
Verbara.Platform.Api.Endpoints.Profile.MfaEnrollEndpoints.MapMfaEnrollEndpoints(v1);
Verbara.Platform.Api.Endpoints.Profile.ProfileSessionsEndpoints.MapProfileSessionsEndpoints(v1);
Verbara.Platform.Api.Endpoints.Profile.ProfileRecoveryCodesEndpoints.MapProfileRecoveryCodesEndpoints(v1);
v1.MapWebhookEndpoints();
v1.MapConversationEndpoints();
v1.MapAgentEndpoints();
v1.MapAdminEndpoints();
v1.MapQueueMembersEndpoints();
v1.MapFlowEndpoints();
v1.MapChannelConfigEndpoints();
v1.MapContactEndpoints();
v1.MapDispositionEndpoints();
v1.MapSetupEndpoints();
v1.MapManagementTenantEndpoints();
v1.MapManagementSystemEndpoints();
v1.MapManagementClusterEndpoints();
v1.MapManagementApiKeyEndpoints();
v1.MapAgentAssistFeatureEndpoints();
v1.MapSseEndpoints();

// Plan 32C — SignalR PlatformHub for supervisor + presence real-time channels.
// JWT validation supports both ?token= (legacy) and ?access_token= (SignalR default)
// via AuthSchemeConfiguration.
app.MapHub<PlatformHub>("/hubs/platform");
v1.MapMediaEndpoints();
v1.MapCampaignEndpoints();
v1.MapCallAttemptEndpoints();
v1.MapDncListEndpoints();
v1.MapCallerIdPoolEndpoints();
v1.MapHolidayCalendarEndpoints();
v1.MapDialerSettingsEndpoints();
v1.MapTrunkEndpoints();
v1.MapOutboundRouteEndpoints();
v1.MapRecordingEndpoints();
v1.MapAnalyticsEndpoints();
v1.MapCallAnalyticsEndpoints();
v1.MapAnalyticsLiveEndpoints();
v1.MapQueueMetricsEndpoints();
v1.MapBotEndpoints();
v1.MapKnowledgeBaseEndpoints();
v1.MapAgentAssistEndpoints();
v1.MapSupervisorEndpoints();
v1.MapSkillEndpoints();
v1.MapAuditEndpoints();
// R5.2 PB.1 — audit log viewer + export (audit.read / audit.export gated).
Verbara.Platform.Api.Endpoints.Audit.AuditAdminEndpoints.MapAuditAdminEndpoints(v1);
v1.MapSurveyEndpoints();
v1.MapScheduledReportEndpoints();
v1.MapRealtimeEndpoints();
v1.MapAuthAdminEndpoints();
// R5.2 PA.1 — MFA admin surface (PlatformAdmin + security.mfa.admin permission).
Verbara.Platform.Api.Endpoints.Mfa.MfaAdminEndpoints.MapMfaAdminEndpoints(v1);
// R5.2 PC.1 — retention admin surface (retention.read / retention.manage gated).
Verbara.Platform.Api.Endpoints.Retention.RetentionAdminEndpoints.MapRetentionAdminEndpoints(v1);
// R5.4 S5.9 — JWT signing-key rotation admin surface (security.jwt.rotate gated).
Verbara.Platform.Api.Endpoints.Security.JwtKeyEndpoints.MapJwtKeyEndpoints(v1);
v1.MapOidcEndpoints();
v1.MapRbacEndpoints();
v1.MapUsersMeEndpoint();
v1.MapManagementBillingEndpoints();
v1.MapManagementImpersonationEndpoints();
v1.MapWebhookSubscriptionEndpoints();
v1.MapManagementWebhookEndpoints();
v1.MapWebhookEventTypeEndpoints();
v1.MapGdprEndpoints();
v1.MapTenantSettingsEndpoints();
v1.MapManagementTenantSettingsEndpoints();
v1.MapManagementTenantIpAllowlistEndpoints();
v1.MapPartnerCustomerEndpoints();
v1.MapPartnerBillingEndpoints();
v1.MapPartnerRevenueEndpoints();
v1.MapPartnerSettingsEndpoints();
v1.MapBrandingEndpoints();
v1.MapNotificationEndpoints();
v1.MapOnboardingEndpoints();
v1.MapWebChatEndpoints();
v1.MapCannedResponseEndpoints();
v1.MapCaseEndpoints();

// WebSocket endpoint for WebChat (outside versioned API group)
app.MapWebChatWebSocket();

// ─── RBAC seed: permissions, role templates (Postgres only) ──────────────────
if (!app.Environment.IsEnvironment("Testing"))
{
    var npgsqlDs = app.Services.GetService<NpgsqlDataSource>();
    if (npgsqlDs is not null)
    {
        try
        {
            await Verbara.Platform.Storage.Postgres.Seeds.RbacSeederOrchestrator
                .SeedRbacAsync(npgsqlDs, CancellationToken.None);
            Console.WriteLine("RBAC seeder: permissions, templates, and role migration complete.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RBAC seeder skipped: {ex.Message}");
        }
    }
}

app.Run();

/// <summary>No-op trunk store used when Dialer is not configured.</summary>
file sealed class InMemoryTrunkStore : TrunkStoreBase
{
    public override ValueTask<IReadOnlyList<Trunk>> ListAsync(string tenantId, CancellationToken ct = default)
        => new(Array.Empty<Trunk>());

    public override ValueTask<IReadOnlyList<Trunk>> ListActiveAsync(string tenantId, CancellationToken ct = default)
        => new(Array.Empty<Trunk>());
}

// Expose Program for WebApplicationFactory in tests
public partial class Program { }
