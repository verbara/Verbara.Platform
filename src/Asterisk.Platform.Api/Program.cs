using Asp.Versioning;
using Asterisk.Platform.Api.Auth;
using Asterisk.Platform.Identity.Auth;
using Microsoft.AspNetCore.DataProtection;
using Asterisk.Platform.Api.Endpoints;
using Asterisk.Platform.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using Microsoft.AspNetCore.RateLimiting;
using Asterisk.Platform.Bot;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Platform.Routing.Inbound;
using Asterisk.Platform.Storage.InMemory;
using Asterisk.Platform.Storage.Postgres;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Media;
using Asterisk.Platform.Switchboard;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Identity.DataProtection;
using Asterisk.Platform.Identity.DependencyInjection;
using Asterisk.Platform.Identity.OidcTokenExchange;
using Asterisk.Platform.Identity.Redis.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Queues.Services;
using Asterisk.Sdk.Hosting;
using Asterisk.Platform.KnowledgeBase;
using Asterisk.Platform.Surveys;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core.Reports;
using Asterisk.Sdk.Pro.Dialer.DependencyInjection;
using Asterisk.Sdk.Pro.Dialer.Storage.Postgres.DependencyInjection;
using Asterisk.Sdk.Pro.EventStore.DependencyInjection;
using Asterisk.Sdk.Pro.EventStore.Postgres.DependencyInjection;
using Asterisk.Sdk.Pro.Analytics.DependencyInjection;
using Asterisk.Sdk.Pro.Analytics.Live;
using Asterisk.Sdk.Pro.Analytics.Storage.Postgres.DependencyInjection;
using Asterisk.Sdk.Pro.Analytics.Storage.Postgres.Live;
using Asterisk.Sdk.Pro.CallAnalytics.DependencyInjection;
using Asterisk.Sdk.Pro.CallAnalytics.Storage.Postgres.DependencyInjection;
using Asterisk.Sdk.Pro.AgentAssist.DependencyInjection;
using Asterisk.Sdk.Pro.AgentAssist.Storage.Postgres.DependencyInjection;
using Asterisk.Sdk.Pro.Licensing.DependencyInjection;
using Asterisk.Sdk.Resilience.DependencyInjection;
using Asterisk.Sdk.Pro.Storage.Common.Retention.DependencyInjection;
using Asterisk.Sdk.Pro.Dialer.Storage.Postgres.Retention;
using Asterisk.Sdk.Pro.EventStore.Postgres.Retention;
using Asterisk.Sdk.Pro.Analytics.Storage.Postgres.Retention;
using Asterisk.Sdk.Pro.CallAnalytics.Storage.Postgres.Retention;
using Asterisk.Sdk.Pro.AgentAssist.Storage.Postgres.Retention;
using Asterisk.Platform.Api.Serialization;
using Asterisk.Platform.Api.Services;
using Asterisk.Sdk.Pro.AgentAssist.Engine;
using Asterisk.Sdk.Pro.Routing.Skills;
using Asterisk.Sdk.Pro.Realtime;
using Asterisk.Sdk.Pro.Realtime.DependencyInjection;
using Asterisk.Sdk.Pro.Realtime.Storage.Postgres.DependencyInjection;
using Asterisk.Sdk.Pro.Realtime.Models;
using Asterisk.Sdk.Pro.Realtime.Decorators;
using Asterisk.Sdk.Pro.Realtime.Engine;
using Asterisk.Sdk.Pro.Dialer.Models;
using Asterisk.Sdk.Pro.Dialer.Routing;
using Asterisk.Sdk.Pro.Dialer.Storage.Postgres;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.Pro.Cluster;
using Asterisk.Sdk.Pro.Cluster.DependencyInjection;
using Asterisk.Sdk.Pro.Cluster.Storage.Postgres.DependencyInjection;
using Asterisk.Platform.Channels.WebChat;
using Asterisk.Sdk.Pro.MultiTenant;
using Asterisk.Sdk.Pro.MultiTenant.DependencyInjection;
using Asterisk.Sdk.Push.Hosting;
using Asterisk.Sdk.Push.Authz;
using Asterisk.Sdk.Pro.Push.SignalR.DependencyInjection;
using Asterisk.Sdk.Pro.Push.SignalR.Hubs;
using Asterisk.Sdk.Pro.Push.SignalR.Bridges;
using Asterisk.Platform.Api.Hubs;
using Asterisk.Sdk.OpenTelemetry;
using Asterisk.Sdk.Pro.OpenTelemetry;

var builder = WebApplication.CreateBuilder(args);

// ─── Asterisk SDK Connection (Multi-Server + Sessions via Cluster) ───────────

builder.Services.AddAsteriskMultiServer();
builder.Services.AddAsteriskSessionsMultiServer();

// ─── Core Platform Services ──────────────────────────────────────────────────

// Asterisk.Sdk.Push: in-process push event bus + delivery filter abstractions.
// MUST precede AddPlatformCore() so the IPushEventBus dependency is available
// for the PlatformEventBus DI ctor and the platform-specific delivery filter
// can override the SDK default.
builder.Services.AddAsteriskPush();
// Asterisk.Sdk.Pro.Push.SignalR: PlatformHub + Phoenix-style Presence CRDT.
// Registers PresenceTracker (singleton), heartbeat + merge HostedServices,
// topic registration, SignalR server with ProPresenceJsonContext JSON resolver.
builder.Services.AddAsteriskProPushSignalR(o =>
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
builder.Services.AddSingleton<Asterisk.Sdk.Pro.Push.SignalR.Authz.IAgentTenantResolver,
    Asterisk.Platform.Api.Authz.CachedAgentTenantResolver>();
builder.Services.AddSingleton<Asterisk.Sdk.Pro.Push.SignalR.Authz.IHubAuditSink,
    Asterisk.Platform.Api.Authz.PlatformHubAuditSink>();
builder.Services.AddPlatformCore();
builder.Services.AddPlatformConversations();
builder.Services.AddPlatformChannels();
builder.Services.AddPlatformQueues();
builder.Services.AddInboundRouting();
builder.Services.AddSwitchboard();
builder.Services.AddPlatformBot();
builder.Services.AddHostedService<Asterisk.Platform.Api.Services.BotAnalyticsPersistenceService>();
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
    builder.Services.Configure<Asterisk.Platform.Channels.Sms.Providers.TwilioOptions>(o =>
    {
        o.AccountSid = twilioSection["AccountSid"]!;
        o.AuthToken = twilioSection["AuthToken"]!;
    });
    builder.Services.AddHttpClient("twilio");
    builder.Services.AddSingleton<Asterisk.Platform.Channels.Sms.ISmsProvider,
        Asterisk.Platform.Channels.Sms.Providers.TwilioSmsProvider>();
    // Transient-retry policy for Twilio HTTP calls (v1.9.1 Frente A).
    Asterisk.Platform.Channels.Sms.ServiceCollectionExtensions.AddTwilioResiliencePolicy(builder.Services);
}

// ─── GDPR Services ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<IGdprExportService, GdprExportService>();
builder.Services.AddSingleton<IGdprPurgeService, GdprPurgeService>();
builder.Services.AddKeyedSingleton<IGdprExportFormatter, JsonGdprExportFormatter>("json");
builder.Services.AddKeyedSingleton<IGdprExportFormatter, CsvGdprExportFormatter>("csv");
builder.Services.AddHostedService<RetentionPurgeService>();
builder.Services.AddHostedService<AuditRetentionService>();

// ─── Storage ─────────────────────────────────────────────────────────────────
var coreConnectionString = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrEmpty(coreConnectionString))
{
    builder.Services.AddPostgresStorage(coreConnectionString);

    // Apply Platform SQL migrations eagerly (before Pro EnsureSchemaAsync which references Platform tables)
    Asterisk.Platform.Api.Services.DatabaseMigrationService.ApplyMigrations(coreConnectionString);

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

// ─── Pro.Licensing ───────────────────────────────────────────────────────────
var licenseConfig = builder.Configuration.GetSection("Licensing");
var licensePath = licenseConfig["FilePath"] ?? "./license.lic";
var publicKeyPath = licenseConfig["PublicKeyPath"];
var licensePublicKey = !string.IsNullOrEmpty(publicKeyPath) && File.Exists(publicKeyPath)
    ? File.ReadAllBytes(publicKeyPath)
    : Array.Empty<byte>();
builder.Services.AddSingleton(licensePublicKey);

var enforcementMode = Enum.TryParse<Asterisk.Sdk.Pro.Licensing.EnforcementMode>(
    licenseConfig["EnforcementMode"], ignoreCase: true, out var parsedMode)
    ? parsedMode
    : (builder.Environment.IsDevelopment()
        ? Asterisk.Sdk.Pro.Licensing.EnforcementMode.WarnOnly
        : Asterisk.Sdk.Pro.Licensing.EnforcementMode.Enforce);

// If no license file exists and no explicit config, fall back to WarnOnly (community mode)
if (!File.Exists(licensePath) && !licenseConfig.Exists())
    enforcementMode = Asterisk.Sdk.Pro.Licensing.EnforcementMode.WarnOnly;

builder.Services.AddProLicensing(o =>
{
    o.LicenseFilePath = licensePath;
    o.EnforcementMode = enforcementMode;
    o.RevalidationInterval = TimeSpan.TryParse(licenseConfig["RevalidationInterval"], out var interval)
        ? interval
        : TimeSpan.FromHours(6);
});

// ─── Observability — OpenTelemetry tracing + metrics providers ──────────────
// Enrols every SDK ActivitySource + Meter (incl. Asterisk.Sdk.Resilience) plus
// the 10 Pro ActivitySources + 15 Pro meters. Prometheus scraping endpoint
// mapped below via app.MapPrometheusScrapingEndpoint(). OTLP exporter opt-in
// via OTEL_EXPORTER_OTLP_ENDPOINT environment variable at runtime.
builder.Services.AddAsteriskOpenTelemetry(b => b
    .WithAllSources()
    .AddAsteriskProOpenTelemetry()
    .WithPrometheusExporter());

// ─── Pro Hardening — Resilience + LicenseGuard + Retention ──────────────────
// Resilience: meter + TimeProvider for circuit breaker / retry / timeout
// primitives (Asterisk.Sdk.Resilience MIT, migrated from Pro.Resilience via
// ADR-0029 in Pro 1.9.0-pro).
builder.Services.AddAsteriskResilience();

// LicenseGuard: runtime feature check (10s cache + 7d grace by default)
builder.Services.AddProLicenseGuard();

// Retention: orchestrator (DryRun=true by default — flip off in production)
builder.Services.AddProRetention();

// ─── Resilience Policies (v1.9.1 — Frente B/E wraps) ────────────────────────
// flow.http-request: circuit 3/60s + retry 2/500ms + timeout 60s (upper bound).
// Per-call timeout is sourced from flow config, not policy — see HttpRequestNodeHandler.
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    Asterisk.Platform.Flows.Nodes.HttpRequestNodeHandler.ResiliencePolicyKey,
    (_, _) => new Asterisk.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithCircuitBreaker(threshold: 3, openDuration: TimeSpan.FromSeconds(60))
        .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(500))
        .WithTimeout(TimeSpan.FromSeconds(60))
        .Build());

// report.pdf-render: circuit 3/120s + retry 1/1s + timeout 30s.
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    Asterisk.Platform.Api.Services.Reports.HttpPdfReportRenderer.ResiliencePolicyKey,
    (_, _) => new Asterisk.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithCircuitBreaker(threshold: 3, openDuration: TimeSpan.FromSeconds(120))
        .WithRetry(maxAttempts: 1, baseDelay: TimeSpan.FromSeconds(1))
        .WithTimeout(TimeSpan.FromSeconds(30))
        .Build());

// storage.s3: circuit 5/60s + retry 3/500ms + timeout 30s. AWS SDK's built-in retry is
// disabled inside S3MediaStorage (MaxErrorRetry = 0) to avoid double-retry.
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    Asterisk.Platform.Media.S3MediaStorage.ResiliencePolicyKey,
    (_, _) => new Asterisk.Sdk.Resilience.ResiliencePolicyBuilder()
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
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    Asterisk.Platform.Api.Services.HttpEmailService.ResiliencePolicyKey,
    (_, _) => new Asterisk.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithCircuitBreaker(threshold: 5, openDuration: TimeSpan.FromSeconds(45))
        .WithRetry(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(300))
        .WithTimeout(TimeSpan.FromSeconds(10))
        .Build());
builder.Services.AddSingleton<Asterisk.Platform.Core.Email.IEmailService,
    Asterisk.Platform.Api.Services.HttpEmailService>();
builder.Services.AddSingleton<Asterisk.Platform.Core.Email.IEmailTemplateService,
    Asterisk.Platform.Api.Services.HttpEmailTemplateService>();
builder.Services.AddSingleton<Asterisk.Platform.Api.Services.NotificationService>();
builder.Services.AddSingleton<Asterisk.Platform.Core.Notifications.INotificationService>(
    sp => sp.GetRequiredService<Asterisk.Platform.Api.Services.NotificationService>());
builder.Services.AddKeyedSingleton<Asterisk.Platform.Core.Reports.IReportRenderer,
    Asterisk.Platform.Api.Services.Reports.HttpPdfReportRenderer>("pdf");
builder.Services.AddKeyedSingleton<Asterisk.Platform.Core.Reports.IReportRenderer,
    Asterisk.Platform.Api.Services.Reports.CsvReportRenderer>("csv");
// IScheduledReportStore — Postgres when available, InMemory otherwise (registered below with storage)
builder.Services.AddSingleton<IReportDataBuilder, Asterisk.Platform.Api.Services.Reports.AgentPerformanceReportBuilder>();
builder.Services.AddSingleton<IReportDataBuilder, Asterisk.Platform.Api.Services.Reports.QueueAnalyticsReportBuilder>();
builder.Services.AddSingleton<IReportDataBuilder, Asterisk.Platform.Api.Services.Reports.ConversationSummaryReportBuilder>();
builder.Services.AddSingleton<Asterisk.Platform.Api.Services.Reports.ReportDataBuilderRegistry>();
builder.Services.AddSingleton<Asterisk.Platform.Api.Services.Reports.ReportSchedulerService>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Asterisk.Platform.Api.Services.Reports.ReportSchedulerService>());

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

// ─── Asterisk Capacity Sync (voice ↔ digital) ──────────────────────────────
builder.Services.AddHostedService<AsteriskCapacitySyncService>();

// ─── Outbound Webhooks ──────────────────────────────────────────────────────
builder.Services.Configure<Asterisk.Platform.Core.Webhooks.CircuitBreakerOptions>(o =>
{
    var s = builder.Configuration.GetSection("Webhooks:CircuitBreaker");
    if (int.TryParse(s["FailureThreshold"], out var ft)) o.FailureThreshold = ft;
    if (int.TryParse(s["CooldownSeconds"], out var cs)) o.CooldownSeconds = cs;
    if (int.TryParse(s["MaxCooldownSeconds"], out var mcs)) o.MaxCooldownSeconds = mcs;
    if (double.TryParse(s["CooldownMultiplier"], System.Globalization.CultureInfo.InvariantCulture, out var cm)) o.CooldownMultiplier = cm;
});
builder.Services.AddSingleton<Asterisk.Platform.Core.Webhooks.CircuitBreakerPolicy>();
builder.Services.AddSingleton<WebhookDispatcher>();
// Transient-retry policy for the HTTP send call inside WebhookDeliveryService.
// Orthogonal to the per-subscription CircuitBreakerPolicy above (which persists
// circuit state on the WebhookSubscription entity — a product feature).
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    WebhookDeliveryService.ResiliencePolicyKey,
    (_, _) => new Asterisk.Sdk.Resilience.ResiliencePolicyBuilder()
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
        opt.ApplicationName = "Asterisk.Platform.Testing";
        opt.UseEphemeralKeysForTesting();
    });
}
else
{
    builder.Services.AddDbContext<PlatformDataProtectionDbContext>(opt =>
        opt.UseNpgsql(coreConnectionString));
    builder.Services.AddPlatformDataProtection(opt =>
    {
        opt.ApplicationName = "Asterisk.Platform";
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
builder.Services.Configure<Asterisk.Platform.Core.Configuration.AgentAssistOptions>(
    builder.Configuration.GetSection("AgentAssist"));
builder.Services.AddSingleton<Asterisk.Platform.Api.Services.AgentAssist.AgentAssistCredentialsProtector>();
builder.Services.AddSingleton<Asterisk.Sdk.Pro.AgentAssist.Features.IAgentAssistFeatureToggle,
    Asterisk.Platform.Api.Services.AgentAssist.InMemoryAgentAssistFeatureToggle>();

var jwtKeyDirectory = builder.Configuration["Auth:KeyDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
builder.Services.AddSingleton<JwtTokenService>(sp => new JwtTokenService(
    jwtKeyDirectory,
    sp.GetRequiredService<IDataProtectionProvider>(),
    sp.GetRequiredService<IJtiRevocationCache>()));
builder.Services.AddSingleton<RefreshTokenService>();
builder.Services.AddSingleton<AuthEventService>();
builder.Services.AddSingleton<AccountLockoutService>();
builder.Services.AddSingleton<SessionService>();
// R5.2 PA.1 — MFA admin service backing /management/mfa endpoints.
builder.Services.AddSingleton<
    Asterisk.Platform.Api.Endpoints.Mfa.IMfaAdminService,
    Asterisk.Platform.Api.Endpoints.Mfa.MfaAdminService>();

// R5.2 PB.1 — audit log viewer query service backing /admin/audit/events
// + /admin/audit/export. Wraps IAuditStore with the presentation-layer DTO
// so the React DataTable can render rows directly without re-shaping.
builder.Services.AddSingleton<
    Asterisk.Platform.Api.Endpoints.Audit.IAuditQueryService,
    Asterisk.Platform.Api.Endpoints.Audit.DefaultAuditQueryService>();

// R5.2 PB.2 + C.7 — admin impersonation session store + auto-timeout sweep.
// Default in-memory store (single-process safe); multi-instance Platform
// deployments swap this for a Redis or Postgres-backed store via override.
builder.Services.AddSingleton<
    Asterisk.Platform.Core.Impersonation.IImpersonationSessionStore,
    Asterisk.Platform.Core.Impersonation.InMemoryImpersonationSessionStore>();
builder.Services.AddHostedService<
    Asterisk.Platform.Api.Services.ImpersonationSessionTimeoutService>();
// TimeProvider is required by the impersonation endpoints + the timeout
// sweep; register the system clock if no test-time replacement was injected.
if (!builder.Services.Any(d => d.ServiceType == typeof(TimeProvider)))
    builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TenantProvisioningService>();
builder.Services.AddSingleton<ITenantLifecycleHandler>(sp => sp.GetRequiredService<TenantProvisioningService>());

// ─── MFA Policy Evaluator ────────────────────────────────────────────────────
builder.Services.AddSingleton<Asterisk.Platform.Identity.Mfa.IMfaPolicyEvaluator,
    Asterisk.Platform.Identity.Mfa.TenantAuthConfigMfaPolicyEvaluator>();

// R5.2 PA.2 — Recovery code generation/hashing for profile-scoped MFA wizard.
builder.Services.AddSingleton<Asterisk.Platform.Identity.Mfa.IRecoveryCodeService,
    Asterisk.Platform.Identity.Mfa.RecoveryCodeService>();

// ─── MFA / Password-Reset Token Caches ─────────────────────────────────────
// Default: in-memory implementations (single-instance safe). When
// ConnectionStrings:IdentityRedis is set, AddAsteriskPlatformIdentityRedis
// replaces both registrations with Redis-backed impls so MFA challenge +
// password-reset tokens survive failover across multiple API instances.
builder.Services.AddSingleton<Asterisk.Platform.Identity.Mfa.IMfaPendingCache,
    Asterisk.Platform.Identity.Mfa.InMemoryMfaPendingCache>();
builder.Services.AddSingleton<Asterisk.Platform.Identity.Mfa.IPasswordResetCache,
    Asterisk.Platform.Identity.Mfa.InMemoryPasswordResetCache>();

var identityRedisConn = builder.Configuration.GetConnectionString("IdentityRedis");
if (!string.IsNullOrWhiteSpace(identityRedisConn))
{
    builder.Services.AddAsteriskPlatformIdentityRedis(o =>
    {
        o.ConnectionString = identityRedisConn;
        var prefix = builder.Configuration["Identity:Redis:KeyPrefix"];
        if (!string.IsNullOrWhiteSpace(prefix))
            o.KeyPrefix = prefix;
    });
}

// ─── OIDC SSO Services ──────────────────────────────────────────────────────
builder.Services.AddHttpClient("oidc");
// Transient-retry policy for OIDC token-exchange POST — retry 2 attempts (500ms base),
// 10s per-attempt timeout, circuit opens after 3 consecutive failures for 120s.
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    OidcTokenExchangeService.ResiliencePolicyKey,
    (_, _) => new Asterisk.Sdk.Resilience.ResiliencePolicyBuilder()
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
static Asterisk.Sdk.Resilience.ResiliencePolicy BuildDefaultWorkerPolicy() =>
    new Asterisk.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithCircuitBreaker(threshold: 5, openDuration: TimeSpan.FromSeconds(60))
        .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(500))
        .WithTimeout(TimeSpan.FromSeconds(10))
        .Build();
static Asterisk.Sdk.Resilience.ResiliencePolicy BuildDbHeavyWorkerPolicy() =>
    new Asterisk.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithCircuitBreaker(threshold: 3, openDuration: TimeSpan.FromSeconds(120))
        .WithRetry(maxAttempts: 1, baseDelay: TimeSpan.FromSeconds(2))
        .WithTimeout(TimeSpan.FromSeconds(20))
        .Build();
static Asterisk.Sdk.Resilience.ResiliencePolicy BuildHourlyWorkerPolicy() =>
    new Asterisk.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithCircuitBreaker(threshold: 3, openDuration: TimeSpan.FromSeconds(600))
        .WithRetry(maxAttempts: 1, baseDelay: TimeSpan.FromSeconds(5))
        .WithTimeout(TimeSpan.FromSeconds(60))
        .Build();

// Default-budget workers
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    ConversationTimeoutWorker.ResiliencePolicyKey, (_, _) => BuildDefaultWorkerPolicy());
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    QueueDistributionWorker.ResiliencePolicyKey, (_, _) => BuildDefaultWorkerPolicy());
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    Asterisk.Platform.Api.Services.Reports.ReportSchedulerService.ResiliencePolicyKey,
    (_, _) => BuildDefaultWorkerPolicy());
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    AsteriskCapacitySyncService.ResiliencePolicyKey, (_, _) => BuildDefaultWorkerPolicy());
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    RealtimeStateBridge.ResiliencePolicyKey, (_, _) => BuildDefaultWorkerPolicy());
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    CampaignMetricsPoller.ResiliencePolicyKey, (_, _) => BuildDefaultWorkerPolicy());
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    AgentAssistBridge.ResiliencePolicyKey, (_, _) => BuildDefaultWorkerPolicy());
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    Asterisk.Platform.Automation.TimerPollingService.ResiliencePolicyKey,
    (_, _) => BuildDefaultWorkerPolicy());

// DB-heavy workers (long batch DELETEs / bulk inserts)
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    RetentionPurgeService.ResiliencePolicyKey, (_, _) => BuildDbHeavyWorkerPolicy());
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    AuditRetentionService.ResiliencePolicyKey, (_, _) => BuildDbHeavyWorkerPolicy());
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    BotAnalyticsPersistenceService.ResiliencePolicyKey, (_, _) => BuildDbHeavyWorkerPolicy());

// Hourly-cadence worker
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    Asterisk.Platform.Billing.DunningService.ResiliencePolicyKey,
    (_, _) => BuildHourlyWorkerPolicy());

// ─── Pro.Dialer (Outbound Campaigns) ────────────────────────────────────────
var dialerConnectionString = builder.Configuration.GetConnectionString("Dialer") ?? builder.Configuration.GetConnectionString("Postgres") ?? "";
if (!string.IsNullOrEmpty(dialerConnectionString))
{
    builder.Services.UsePostgresDialerStorage(dialerConnectionString);
    builder.Services.AddProDialer(o => { });
    builder.Services.AddDialerRetentionTargets();
    builder.Services.AddHostedService<CampaignMetricsPoller>();
}

// ─── Pro.Cluster ─────────────────────────────────────────────────────────────
builder.Services.AddAsteriskCluster(c =>
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
            nodeOptions.Ari = new Asterisk.Sdk.Ari.Client.AriClientOptions
            {
                BaseUrl = ariBaseUrl,
                Username = ariSection["Username"] ?? "",
                Password = ariSection["Password"] ?? "",
                Application = ariSection["Application"] ?? "asterisk-platform",
            };
        }

        c.InitialNodes["primary"] = nodeOptions;
    }
});
var clusterConn = builder.Configuration.GetConnectionString("Cluster")
    ?? builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrEmpty(clusterConn))
{
    builder.Services.UsePostgresClusterTransport(clusterConn);
}

// ─── Pro.MultiTenant ─────────────────────────────────────────────────────────
builder.Services.AddAsteriskMultiTenant();

// ─── Pro.Realtime (replaces AsteriskRealtimeSyncService) ─────────────────────
builder.Services.AddAsteriskRealtime(o =>
{
    o.ReconcilerIntervalSeconds = 60;
    o.EnableAgentPresenceTracking = false;
});
var realtimeConn = builder.Configuration.GetConnectionString("Realtime")
    ?? builder.Configuration.GetConnectionString("Analytics")
    ?? builder.Configuration.GetConnectionString("Postgres")
    ?? "";
if (!string.IsNullOrEmpty(realtimeConn))
    builder.Services.UsePostgresRealtimeStorage(realtimeConn);
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
var analyticsConnectionString = builder.Configuration.GetConnectionString("Analytics") ?? dialerConnectionString;
if (!string.IsNullOrEmpty(analyticsConnectionString))
{
    builder.Services.UsePostgresEventStore(analyticsConnectionString);
    builder.Services.AddProCallAnalyticsPostgres(analyticsConnectionString);
    builder.Services.UsePostgresAnalyticsStore(analyticsConnectionString);

    // Pro engine registrations — require ICallSessionManager (wired by AddAsteriskSessionsMultiServer)
    builder.Services.AddAsteriskEventStore();
    // ADR-0002 / ADR-0004 §"Pro.Analytics": single-tenant deploys must opt in
    // explicitly. Without WithSingleTenantMode the engine has no DefaultTenantId
    // and LiveQueueSnapshotWriter rejects events with reason=missing_tenant.
    builder.Services.AddAsteriskAnalytics().WithSingleTenantMode("default");
    builder.Services.AddProCallAnalytics();
    builder.Services.AddProAgentAssist(assistBuilder =>
    {
        assistBuilder.WithRuntimeFeatureToggle(sp =>
            sp.GetRequiredService<Asterisk.Sdk.Pro.AgentAssist.Features.IAgentAssistFeatureToggle>());
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
    var liveAnalyticsConnectionString = builder.Configuration.GetConnectionString("AnalyticsLive")
        ?? analyticsConnectionString;
    builder.Services.AddAsteriskProAnalyticsLive();
    builder.Services.UsePostgresProAnalyticsLive(liveAnalyticsConnectionString);

    // AgentAssist Postgres query stores (read-only endpoints for supervisor dashboard)
    builder.Services.AddProAgentAssistPostgres(analyticsConnectionString);

    // Pro Retention targets (v1.8.0-pro) — DryRun=true default via AddProRetention
    builder.Services.AddEventStoreRetentionTargets();
    builder.Services.AddAnalyticsRetentionTargets();
    builder.Services.AddCallAnalyticsRetentionTargets();
    builder.Services.AddAgentAssistRetentionTargets();
}

// ─── Pro Analytics InMemory fallbacks (when no Analytics/Dialer connection string) ──
if (!builder.Services.Any(d => d.ServiceType == typeof(Asterisk.Sdk.Pro.EventStore.ICompletedSessionStore)))
    builder.Services.AddSingleton<Asterisk.Sdk.Pro.EventStore.ICompletedSessionStore, Asterisk.Platform.Storage.InMemory.InMemoryCompletedSessionStore>();
if (!builder.Services.Any(d => d.ServiceType == typeof(Asterisk.Sdk.Pro.Analytics.IIntervalSnapshotStore)))
    builder.Services.AddSingleton<Asterisk.Sdk.Pro.Analytics.IIntervalSnapshotStore, Asterisk.Platform.Storage.InMemory.InMemoryIntervalSnapshotStore>();
if (!builder.Services.Any(d => d.ServiceType == typeof(Asterisk.Sdk.Pro.CallAnalytics.Store.ICallAnalyticsStore)))
    builder.Services.AddSingleton<Asterisk.Sdk.Pro.CallAnalytics.Store.ICallAnalyticsStore, Asterisk.Platform.Storage.InMemory.InMemoryCallAnalyticsStore>();

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
        var policy = sp.GetKeyedService<Asterisk.Sdk.Resilience.ResiliencePolicy>(
            Asterisk.Platform.Media.S3MediaStorage.ResiliencePolicyKey);
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
        Asterisk.Platform.Api.Endpoints.Mfa.MfaAdminEndpoints.AuthorizationPolicy,
        p => p.AddRequirements(new PlatformAdminRequirement("security.mfa.admin")));

    // R5.2 PB.1 — audit log viewer + export. Two policies so the export surface
    // can be revoked independently of read access (compliance scenarios where
    // viewing in-app is fine but mass extract requires extra approval). Both
    // run through PlatformAdminRequirement (host/partner gate + seeded
    // dot-notation permission `audit.read` / `audit.export`).
    options.AddPolicy(
        Asterisk.Platform.Api.Endpoints.Audit.AuditAdminEndpoints.QueryPolicy,
        p => p.AddRequirements(new PlatformAdminRequirement("audit.read")));
    options.AddPolicy(
        Asterisk.Platform.Api.Endpoints.Audit.AuditAdminEndpoints.ExportPolicy,
        p => p.AddRequirements(new PlatformAdminRequirement("audit.export")));

    // R5.2 PB.2 — impersonation admin surface. Same double-lock pattern as
    // PA.1 / PB.1: PlatformAdminRequirement combines host/partner-tenant
    // gating with the seeded `security.impersonation.manage` permission so
    // only platform admins (or partner admins managing their children) with
    // the explicit permission can list / revoke active sessions or read
    // history.
    options.AddPolicy(
        Asterisk.Platform.Api.Endpoints.ManagementImpersonationEndpoints.AdminAuthorizationPolicy,
        p => p.AddRequirements(new PlatformAdminRequirement("security.impersonation.manage")));

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

builder.Services.AddSingleton<Asterisk.Platform.Api.Health.IServiceHeartbeat, Asterisk.Platform.Api.Health.ServiceHeartbeat>();

// Frente D (v1.9.1): resilience-state-aware health checks. The observer listens
// to the Asterisk.Sdk.Resilience meter's circuit.state observable gauge and
// caches per-policy-key state + timestamp so HealthCheck implementations can
// distinguish Healthy / Degraded / Unhealthy based on how long a circuit has
// been open.
builder.Services.Configure<Asterisk.Platform.Api.Health.PlatformHealthCheckOptions>(_ => { });
builder.Services.AddSingleton<Asterisk.Platform.Api.Health.ResilienceStateObserver>();
builder.Services.AddSingleton<Asterisk.Platform.Api.Health.IResilienceStateObserver>(
    sp => sp.GetRequiredService<Asterisk.Platform.Api.Health.ResilienceStateObserver>());
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<Asterisk.Platform.Api.Health.ResilienceStateObserver>());

// Dedicated HealthCheck-owned resilience policy: 2s timeout, no retry, no
// circuit (we want every probe to run). Surfaces Postgres-under-load as
// Unhealthy with a clear reason instead of hanging on the outer HealthCheck
// timeout.
builder.Services.AddKeyedSingleton<Asterisk.Sdk.Resilience.ResiliencePolicy>(
    Asterisk.Platform.Api.Health.PostgresHealthCheck.ResiliencePolicyKey,
    (_, _) => new Asterisk.Sdk.Resilience.ResiliencePolicyBuilder()
        .WithTimeout(TimeSpan.FromSeconds(2))
        .Build());

var healthBuilder = builder.Services.AddHealthChecks()
    .AddCheck<Asterisk.Platform.Api.Health.BackgroundServiceHealthCheck>("services", tags: ["ready"])
    .AddCheck<Asterisk.Platform.Api.Health.AsteriskAmiHealthCheck>("asterisk", tags: ["ready"]);

// Only add Postgres health check if NpgsqlDataSource is registered
if (builder.Services.Any(d => d.ServiceType == typeof(NpgsqlDataSource)))
    healthBuilder.AddCheck<Asterisk.Platform.Api.Health.PostgresHealthCheck>("postgres", tags: ["ready"]);

// ─── Rate Limiting ────────────────────────────────────────────────────────────

builder.Services.AddRateLimiter(TenantRateLimitPolicy.ConfigureRateLimiting);
builder.Services.AddSingleton<Asterisk.Platform.Api.Services.TenantTierCache>();
builder.Services.AddSingleton<Asterisk.Platform.Api.Services.FeatureGateCache>();
builder.Services.AddSingleton<Asterisk.Platform.Core.IFeatureGateService, Asterisk.Platform.Api.Services.DefaultFeatureGateService>();

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
builder.Services.AddOpenApi();

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
    }

    if (configErrors.Count > 0)
        throw new InvalidOperationException(
            $"Production configuration errors:\n  - {string.Join("\n  - ", configErrors)}");
}

// ─── Middleware pipeline ──────────────────────────────────────────────────────

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
app.UseMiddleware<TenantStatusMiddleware>();
app.UseMiddleware<LicenseGateMiddleware>();

app.MapOpenApi();
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
    ResponseWriter = Asterisk.Platform.Api.Health.HealthReportJsonWriter.WriteAsync,
});
// Prometheus scraping endpoint at /metrics — exposes the full SDK/Pro meter
// catalog (resilience, licensing, cluster, push, etc.) registered via
// AddAsteriskOpenTelemetry(...).WithPrometheusExporter() above.
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
Asterisk.Platform.Api.Endpoints.Profile.MfaEnrollEndpoints.MapMfaEnrollEndpoints(v1);
Asterisk.Platform.Api.Endpoints.Profile.ProfileSessionsEndpoints.MapProfileSessionsEndpoints(v1);
Asterisk.Platform.Api.Endpoints.Profile.ProfileRecoveryCodesEndpoints.MapProfileRecoveryCodesEndpoints(v1);
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
Asterisk.Platform.Api.Endpoints.Audit.AuditAdminEndpoints.MapAuditAdminEndpoints(v1);
v1.MapSurveyEndpoints();
v1.MapScheduledReportEndpoints();
v1.MapRealtimeEndpoints();
v1.MapAuthAdminEndpoints();
// R5.2 PA.1 — MFA admin surface (PlatformAdmin + security.mfa.admin permission).
Asterisk.Platform.Api.Endpoints.Mfa.MfaAdminEndpoints.MapMfaAdminEndpoints(v1);
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
            await Asterisk.Platform.Storage.Postgres.Seeds.RbacSeederOrchestrator
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
