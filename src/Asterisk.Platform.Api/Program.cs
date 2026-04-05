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
using Asterisk.Sdk.Hosting;
using Asterisk.Platform.KnowledgeBase;
using Asterisk.Platform.Surveys;
using Asterisk.Platform.Billing;
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
using Asterisk.Sdk.Pro.MultiTenant;
using Asterisk.Sdk.Pro.MultiTenant.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// ─── Asterisk SDK Connection (Multi-Server + Sessions via Cluster) ───────────

builder.Services.AddAsteriskMultiServer();
builder.Services.AddAsteriskSessionsMultiServer();

// ─── Core Platform Services ──────────────────────────────────────────────────

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

// ─── GDPR Services ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<IGdprExportService, GdprExportService>();
builder.Services.AddSingleton<IGdprPurgeService, GdprPurgeService>();
builder.Services.AddHostedService<RetentionPurgeService>();

// ─── Storage ─────────────────────────────────────────────────────────────────
var coreConnectionString = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrEmpty(coreConnectionString))
{
    builder.Services.AddPostgresStorage(coreConnectionString);
    // ITenantStore has no Postgres implementation yet — use InMemory as fallback
    builder.Services.AddSingleton<Asterisk.Sdk.Pro.MultiTenant.ITenantStore,
        Asterisk.Platform.Storage.InMemory.InMemoryTenantStore>();
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

// ─── Pro.Routing — Skill Catalog (in-memory, singleton) ─────────────────────
builder.Services.AddSingleton<SkillCatalogBase>(new InMemorySkillCatalog());

// ─── Agent Assist Config Store (singleton for mutable admin config) ───────────
builder.Services.AddSingleton<AgentAssistConfigStore>();

// ─── System Settings Store (singleton for mutable system settings) ────────────
builder.Services.AddSingleton<SystemSettingsStore>();

// ─── Scheduled Report Store (singleton for mutable report definitions) ────────
builder.Services.AddSingleton<ScheduledReportStore>();

// ─── Outbound Webhooks ──────────────────────────────────────────────────────
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddHostedService<WebhookDeliveryService>();
builder.Services.AddHttpClient("webhooks");

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
}

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
});

// RBAC permission-based authorization
builder.Services.AddSingleton<PermissionResolver>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();

// ─── Health Checks ───────────────────────────────────────────────────────────

builder.Services.AddHealthChecks();

// ─── Rate Limiting ────────────────────────────────────────────────────────────

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddSlidingWindowLimiter("api", o =>
    {
        o.Window = TimeSpan.FromMinutes(1);
        o.SegmentsPerWindow = 6;
        o.PermitLimit = 600;
    });
});

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
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddOpenApi();

var app = builder.Build();

// ─── Middleware pipeline ──────────────────────────────────────────────────────

app.UseMiddleware<VersionRedirectMiddleware>();
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();
app.MapHealthChecks("/health");
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
