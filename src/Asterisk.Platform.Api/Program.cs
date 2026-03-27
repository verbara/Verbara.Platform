using Asterisk.Platform.Api.Auth;
using Asterisk.Platform.Api.Endpoints;
using Asterisk.Platform.Api.Middleware;
using Microsoft.AspNetCore.RateLimiting;
using Asterisk.Platform.Bot;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Platform.Routing.Inbound;
using Asterisk.Platform.Storage.InMemory;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Media;
using Asterisk.Platform.Switchboard;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Queues;
using Asterisk.Sdk.Hosting;
using Asterisk.Platform.KnowledgeBase;
using Asterisk.Platform.Surveys;
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
using Asterisk.Sdk.Pro.Dialer.Routing;
using Asterisk.Sdk.Pro.Dialer.Storage.Postgres;
using Asterisk.Sdk.Pro.Cluster.DependencyInjection;
using Asterisk.Sdk.Pro.MultiTenant.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// ─── Asterisk SDK Connection (AMI + ARI + Sessions) ──────────────────────────

builder.Services.AddAsterisk(builder.Configuration);
builder.Services.AddAsteriskSessions();

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

// ─── In-Memory Storage (zero-infrastructure default) ─────────────────────────

builder.Services.AddInMemoryStorage();

// ─── Pro.Licensing ───────────────────────────────────────────────────────────
builder.Services.AddProLicensing();

// ─── Pro.Routing — Skill Catalog (in-memory, singleton) ─────────────────────
builder.Services.AddSingleton<SkillCatalogBase>(new InMemorySkillCatalog());

// ─── Agent Assist Config Store (singleton for mutable admin config) ───────────
builder.Services.AddSingleton<AgentAssistConfigStore>();

// ─── System Settings Store (singleton for mutable system settings) ────────────
builder.Services.AddSingleton<SystemSettingsStore>();

// ─── Scheduled Report Store (singleton for mutable report definitions) ────────
builder.Services.AddSingleton<ScheduledReportStore>();

// ─── Auth Services ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<PasswordService>();

var jwtKeyDirectory = builder.Configuration["Auth:KeyDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
var jwtTokenService = new JwtTokenService(jwtKeyDirectory);
builder.Services.AddSingleton(jwtTokenService);
builder.Services.AddSingleton<RefreshTokenService>();
builder.Services.AddSingleton<AuthEventService>();
builder.Services.AddSingleton<AccountLockoutService>();
builder.Services.AddSingleton<MfaService>();
builder.Services.AddSingleton<SessionService>();

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
});

// ─── Pro.MultiTenant ─────────────────────────────────────────────────────────
builder.Services.AddAsteriskMultiTenant();

// ─── Pro.Realtime (replaces AsteriskRealtimeSyncService) ─────────────────────
builder.Services.AddAsteriskRealtime(o =>
{
    o.ReconcilerIntervalSeconds = 60;
    o.EnableAgentPresenceTracking = false;
});
var realtimeConn = dialerConnectionString;
if (!string.IsNullOrEmpty(realtimeConn))
    builder.Services.UsePostgresRealtimeStorage(realtimeConn);
builder.Services.AddHostedService<RealtimeStateBridge>();

// Queue membership service + desired state provider for reconciler
builder.Services.AddSingleton<QueueMembershipService>();
builder.Services.AddSingleton<IDesiredStateProvider, PlatformDesiredStateProvider>();

// Trunk decorator — wraps PostgresTrunkStore with Realtime sync
builder.Services.AddSingleton<TrunkStoreBase>(sp =>
    new RealtimeSyncingTrunkStore(
        new PostgresTrunkStore(sp.GetRequiredService<DialerDbContext>()),
        sp.GetRequiredService<IRealtimeSyncService>()));

// ─── Pro EventStore + Analytics + CallAnalytics (engines + Postgres stores) ──
var analyticsConnectionString = builder.Configuration.GetConnectionString("Analytics") ?? dialerConnectionString;
if (!string.IsNullOrEmpty(analyticsConnectionString))
{
    builder.Services.UsePostgresEventStore(analyticsConnectionString);
    builder.Services.AddProCallAnalyticsPostgres(analyticsConnectionString);
    builder.Services.UsePostgresAnalyticsStore(analyticsConnectionString);

    // Pro engine registrations — require ICallSessionManager (wired by AddAsteriskSessions)
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
});

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

// ─── Endpoint mapping ────────────────────────────────────────────────────────

app.MapAuthEndpoints();
app.MapWebhookEndpoints();
app.MapConversationEndpoints();
app.MapAgentEndpoints();
app.MapAdminEndpoints();
app.MapFlowEndpoints();
app.MapChannelConfigEndpoints();
app.MapContactEndpoints();
app.MapDispositionEndpoints();
app.MapSystemEndpoints();
app.MapSseEndpoints();
app.MapMediaEndpoints();
app.MapCampaignEndpoints();
app.MapCallAttemptEndpoints();
app.MapDncListEndpoints();
app.MapCallerIdPoolEndpoints();
app.MapHolidayCalendarEndpoints();
app.MapDialerSettingsEndpoints();
app.MapTrunkEndpoints();
app.MapOutboundRouteEndpoints();
app.MapRecordingEndpoints();
app.MapAnalyticsEndpoints();
app.MapAnalyticsLiveEndpoints();
app.MapQueueMetricsEndpoints();
app.MapBotEndpoints();
app.MapKnowledgeBaseEndpoints();
app.MapAgentAssistEndpoints();
app.MapSupervisorEndpoints();
app.MapSkillEndpoints();
app.MapAuditEndpoints();
app.MapSurveyEndpoints();
app.MapScheduledReportEndpoints();
app.MapRealtimeEndpoints();
app.MapClusterEndpoints();
app.MapTenantEndpoints();
app.MapAuthAdminEndpoints();

// ─── Dev seed: create demo users + API keys for local testing ────────────────
{
    using var scope = app.Services.CreateScope();
    var apiKeyStore = scope.ServiceProvider.GetRequiredService<IApiKeyStore>();
    var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();
    var agentStore = scope.ServiceProvider.GetRequiredService<IAgentStore>();
    var clock = scope.ServiceProvider.GetRequiredService<IClock>();

    var tenantId = new TenantId("demo");

    static string HashKey(string raw) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));

    // ── Admin ──────────────────────────────────────────────────────────────────
    var adminUserId = EntityId.From("demo-user-admin");
    await userStore.SaveAsync(new User
    {
        UserId = adminUserId,
        TenantId = tenantId,
        Email = "admin@demo.local",
        DisplayName = "Demo Admin",
        Role = UserRole.Admin,
        Status = UserStatus.Active,
        CreatedAt = clock.UtcNow,
    }, CancellationToken.None);

    await apiKeyStore.SaveAsync(new ApiKey
    {
        KeyId = EntityId.From("demo-key-admin"),
        TenantId = tenantId,
        Name = "Demo Admin Key",
        HashedKey = HashKey("demo-key-admin"),
        Scopes = ["*"],
        UserId = adminUserId,
        CreatedAt = clock.UtcNow,
    }, CancellationToken.None);

    // ── Supervisor ─────────────────────────────────────────────────────────────
    var supervisorUserId = EntityId.From("demo-user-supervisor");
    await userStore.SaveAsync(new User
    {
        UserId = supervisorUserId,
        TenantId = tenantId,
        Email = "supervisor@demo.local",
        DisplayName = "Demo Supervisor",
        Role = UserRole.Supervisor,
        Status = UserStatus.Active,
        CreatedAt = clock.UtcNow,
    }, CancellationToken.None);

    await apiKeyStore.SaveAsync(new ApiKey
    {
        KeyId = EntityId.From("demo-key-supervisor"),
        TenantId = tenantId,
        Name = "Demo Supervisor Key",
        HashedKey = HashKey("demo-key-supervisor"),
        Scopes = ["*"],
        UserId = supervisorUserId,
        CreatedAt = clock.UtcNow,
    }, CancellationToken.None);

    // ── Agent ──────────────────────────────────────────────────────────────────
    var agentUserId = EntityId.From("demo-user-agent");
    await userStore.SaveAsync(new User
    {
        UserId = agentUserId,
        TenantId = tenantId,
        Email = "agent@demo.local",
        DisplayName = "Demo Agent",
        Role = UserRole.Agent,
        Status = UserStatus.Active,
        CreatedAt = clock.UtcNow,
    }, CancellationToken.None);

    await apiKeyStore.SaveAsync(new ApiKey
    {
        KeyId = EntityId.From("demo-key-agent"),
        TenantId = tenantId,
        Name = "Demo Agent Key",
        HashedKey = HashKey("demo-key-agent"),
        Scopes = ["*"],
        UserId = agentUserId,
        CreatedAt = clock.UtcNow,
    }, CancellationToken.None);

    // ── Demo agent record (for /api/agents/me compatibility) ──────────────────
    await agentStore.SaveAsync(new Agent
    {
        AgentId = EntityId.From("demo-agent"),
        TenantId = tenantId,
        UserId = agentUserId,
        DisplayName = "Demo Agent",
        State = AgentState.Available,
        Skills = ["support"],
        CreatedAt = clock.UtcNow,
    }, CancellationToken.None);

    // ── Sync demo tenant/agent/queue to Asterisk Realtime tables (best-effort) ─
    var syncService = app.Services.GetService<IRealtimeSyncService>();
    if (syncService is not null)
    {
        try
        {
            await syncService.ProvisionTenantAsync("demo");
            await syncService.SyncAgentAsync("demo", "demo-agent", "Demo Agent", "2001", "2001");
            await syncService.SyncQueueAsync("demo", "support", new RealtimeQueueOptions
            {
                Timeout = 30, Wrapuptime = 15, Servicelevel = 20
            });
            await syncService.AddQueueMemberAsync("demo", "support", "demo-agent", "Demo Agent");
            Console.WriteLine("Asterisk Realtime: demo tenant provisioned.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Asterisk Realtime sync skipped: {ex.Message}");
        }
    }

    Console.WriteLine("Demo API keys seeded: demo-key-admin | demo-key-supervisor | demo-key-agent");
}

app.Run();

// Expose Program for WebApplicationFactory in tests
public partial class Program { }
