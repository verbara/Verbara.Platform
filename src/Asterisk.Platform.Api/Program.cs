using Asp.Versioning;
using Asterisk.Platform.Api.Auth;
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
using Asterisk.Platform.Identity.OidcTokenExchange;
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
using Asterisk.Sdk.Pro.Analytics.Storage.Postgres.DependencyInjection;
using Asterisk.Sdk.Pro.CallAnalytics.DependencyInjection;
using Asterisk.Sdk.Pro.CallAnalytics.Storage.Postgres.DependencyInjection;
using Asterisk.Sdk.Pro.AgentAssist.DependencyInjection;
using Asterisk.Sdk.Pro.AgentAssist.Storage.Postgres.DependencyInjection;
using Asterisk.Sdk.Pro.Licensing.DependencyInjection;
using Asterisk.Sdk.Pro.Resilience.DependencyInjection;
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
using Asterisk.Platform.Api.Hubs;

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
// Override the SDK default AllowAllSubscriptionAuthorizer with RBAC-aware authorizer.
builder.Services.AddSingleton<ISubscriptionAuthorizer, RbacSubscriptionAuthorizer>();
// Replace Pro.Push.SignalR's NotImplementedSupervisorCoordinator default with the
// Platform concrete impl that delegates to AgentAssistSupervisor.
builder.Services.AddSingleton<ISupervisorCoordinator, PlatformSupervisorCoordinator>();
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

// ─── Pro Hardening (v1.8.0-pro) — Resilience + LicenseGuard + Retention ─────
// Resilience: meter + TimeProvider for circuit breaker / retry / timeout primitives
builder.Services.AddProResilience();

// LicenseGuard: runtime feature check (10s cache + 7d grace by default)
builder.Services.AddProLicenseGuard();

// Retention: orchestrator (DryRun=true by default — flip off in production)
builder.Services.AddProRetention();

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
builder.Services.AddHostedService<WebhookDeliveryService>();
builder.Services.AddHttpClient("webhooks");
builder.Services.AddHttpClient("EmailAttachments", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.MaxResponseContentBufferSize = 25 * 1024 * 1024;
});

// ─── Auth Services ──────────────────────────────────────────────────────────
// PasswordService and MfaService are static — no DI registration needed

var jwtKeyDirectory = builder.Configuration["Auth:KeyDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
var jwtTokenService = new JwtTokenService(jwtKeyDirectory);
builder.Services.AddSingleton(jwtTokenService);
builder.Services.AddSingleton<RefreshTokenService>();
builder.Services.AddSingleton<AuthEventService>();
builder.Services.AddSingleton<AccountLockoutService>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<TenantProvisioningService>();
builder.Services.AddSingleton<ITenantLifecycleHandler>(sp => sp.GetRequiredService<TenantProvisioningService>());

// ─── OIDC SSO Services ──────────────────────────────────────────────────────
builder.Services.AddHttpClient("oidc");
builder.Services.AddDataProtection();
builder.Services.AddSingleton<IOidcTokenExchangeService, OidcTokenExchangeService>();
builder.Services.AddSingleton<IOidcUserProvisioningService, OidcUserProvisioningService>();

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
    builder.Services.AddAsteriskAnalytics();
    builder.Services.AddProCallAnalytics();
    // TODO: AddProAgentAssist() requires a SpeechRecognizer implementation — skipped until STT provider is configured

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
    builder.Services.AddSingleton<IMediaStorage>(
        new S3MediaStorage(s3Bucket, s3Endpoint, s3Region, s3ForcePathStyle));
}

// ─── Authentication (JWT + API key dual-scheme) ──────────────────────────────

builder.Services.AddDynamicAuth(jwtTokenService);

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
    options.AddPolicy("SupervisorPlus", p => p.RequireRole("Admin", "Supervisor"));
    options.AddPolicy("Authenticated", p => p.RequireAuthenticatedUser());
    options.AddPolicy("PlatformAdminOnly", p =>
        p.AddRequirements(new PlatformAdminRequirement()));
    options.AddPolicy("PartnerAdminOnly", p =>
        p.AddRequirements(new PartnerAdminRequirement()));

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
});
app.MapGet("/metrics", () => Results.Ok(new
{
    status = "ok",
    timestamp = DateTimeOffset.UtcNow,
    uptime_seconds = (long)(DateTimeOffset.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
})).ExcludeFromDescription();

// ─── Versioned route group ───────────────────────────────────────────────────

var versionSet = app.NewApiVersionSet()
    .HasApiVersion(new ApiVersion(1, 0))
    .ReportApiVersions()
    .Build();

var v1 = app.MapGroup("/api/v{version:apiVersion}")
    .WithApiVersionSet(versionSet);

// ─── Endpoint mapping ────────────────────────────────────────────────────────

v1.MapAuthEndpoints();
v1.MapWebhookEndpoints();
v1.MapConversationEndpoints();
v1.MapAgentEndpoints();
v1.MapAdminEndpoints();
v1.MapFlowEndpoints();
v1.MapChannelConfigEndpoints();
v1.MapContactEndpoints();
v1.MapDispositionEndpoints();
v1.MapSetupEndpoints();
v1.MapManagementTenantEndpoints();
v1.MapManagementSystemEndpoints();
v1.MapManagementClusterEndpoints();
v1.MapManagementApiKeyEndpoints();
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
v1.MapAnalyticsLiveEndpoints();
v1.MapQueueMetricsEndpoints();
v1.MapBotEndpoints();
v1.MapKnowledgeBaseEndpoints();
v1.MapAgentAssistEndpoints();
v1.MapSupervisorEndpoints();
v1.MapSkillEndpoints();
v1.MapAuditEndpoints();
v1.MapSurveyEndpoints();
v1.MapScheduledReportEndpoints();
v1.MapRealtimeEndpoints();
v1.MapAuthAdminEndpoints();
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
