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
using Asterisk.Sdk.Pro.Dialer.Models;
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
builder.Services.AddSingleton<byte[]>(Array.Empty<byte>());
builder.Services.AddProLicensing(o => o.EnforcementMode = Asterisk.Sdk.Pro.Licensing.EnforcementMode.Disabled);

// ─── Pro.Routing — Skill Catalog (in-memory, singleton) ─────────────────────
builder.Services.AddSingleton<SkillCatalogBase>(new InMemorySkillCatalog());

// ─── Agent Assist Config Store (singleton for mutable admin config) ───────────
builder.Services.AddSingleton<AgentAssistConfigStore>();

// ─── System Settings Store (singleton for mutable system settings) ────────────
builder.Services.AddSingleton<SystemSettingsStore>();

// ─── Scheduled Report Store (singleton for mutable report definitions) ────────
builder.Services.AddSingleton<ScheduledReportStore>();

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

// ─── Pro.Dialer (Outbound Campaigns) ────────────────────────────────────────
var dialerConnectionString = builder.Configuration.GetConnectionString("Dialer") ?? builder.Configuration.GetConnectionString("Postgres") ?? "";
if (!string.IsNullOrEmpty(dialerConnectionString))
{
    builder.Services.UsePostgresDialerStorage(dialerConnectionString);
    builder.Services.AddProDialer(o => { });
    builder.Services.AddHostedService<CampaignMetricsPoller>();
}

// ─── Pro.Cluster ─────────────────────────────────────────────────────────────
builder.Services.AddAsteriskMultiServer();
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
app.MapSetupEndpoints();
app.MapManagementTenantEndpoints();
app.MapManagementSystemEndpoints();
app.MapManagementClusterEndpoints();
app.MapManagementApiKeyEndpoints();
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
app.MapAuthAdminEndpoints();
app.MapOidcEndpoints();
app.MapRbacEndpoints();
app.MapUsersMeEndpoint();

// ─── Dev seed: create demo users + API keys for local testing ────────────────
{
    using var scope = app.Services.CreateScope();
    var apiKeyStore = scope.ServiceProvider.GetRequiredService<IApiKeyStore>();
    var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();
    var agentStore = scope.ServiceProvider.GetRequiredService<IAgentStore>();
    var configStore = scope.ServiceProvider.GetRequiredService<ITenantAuthConfigStore>();
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
        PasswordHash = PasswordService.HashPassword("DemoAdmin2026!"),
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
        PasswordHash = PasswordService.HashPassword("DemoSupervisor2026!"),
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

    // ── Agents (6 agents mapped to SIP extensions) ────────────────────────────
    var agentDefs = new[]
    {
        (userId: "demo-user-maria", email: "maria.garcia@demo.local", name: "Maria Garcia", agentId: "demo-agent-maria", ext: "2001", sipPwd: "demo2001", skill: "sales", keyId: "demo-key-maria", keyName: "Demo Maria Key"),
        (userId: "demo-user-carlos", email: "carlos.lopez@demo.local", name: "Carlos Lopez", agentId: "demo-agent-carlos", ext: "2002", sipPwd: "demo2002", skill: "sales", keyId: "demo-key-carlos", keyName: "Demo Carlos Key"),
        (userId: "demo-user-ana", email: "ana.martinez@demo.local", name: "Ana Martinez", agentId: "demo-agent-ana", ext: "2003", sipPwd: "demo2003", skill: "sales", keyId: "demo-key-ana", keyName: "Demo Ana Key"),
        (userId: "demo-user-pedro", email: "pedro.ruiz@demo.local", name: "Pedro Ruiz", agentId: "demo-agent-pedro", ext: "3001", sipPwd: "demo3001", skill: "support", keyId: "demo-key-pedro", keyName: "Demo Pedro Key"),
        (userId: "demo-user-lucia", email: "lucia.fernandez@demo.local", name: "Lucia Fernandez", agentId: "demo-agent-lucia", ext: "3002", sipPwd: "demo3002", skill: "support", keyId: "demo-key-lucia", keyName: "Demo Lucia Key"),
        (userId: "demo-user-demo", email: "demo.agent@demo.local", name: "Demo Agent", agentId: "demo-agent-demo", ext: "3003", sipPwd: "demo3003", skill: "support", keyId: "demo-key-demo", keyName: "Demo Agent Key"),
    };

    foreach (var a in agentDefs)
    {
        var uid = EntityId.From(a.userId);
        await userStore.SaveAsync(new User
        {
            UserId = uid,
            TenantId = tenantId,
            Email = a.email,
            DisplayName = a.name,
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            PasswordHash = PasswordService.HashPassword("DemoAgent2026!"),
            CreatedAt = clock.UtcNow,
        }, CancellationToken.None);

        await apiKeyStore.SaveAsync(new ApiKey
        {
            KeyId = EntityId.From(a.keyId),
            TenantId = tenantId,
            Name = a.keyName,
            HashedKey = HashKey(a.keyId),
            Scopes = ["*"],
            UserId = uid,
            CreatedAt = clock.UtcNow,
        }, CancellationToken.None);

        await agentStore.SaveAsync(new Agent
        {
            AgentId = EntityId.From(a.agentId),
            TenantId = tenantId,
            UserId = uid,
            DisplayName = a.name,
            State = AgentState.Available,
            Skills = [a.skill],
            Extension = a.ext,
            SipPassword = a.sipPwd,
            CreatedAt = clock.UtcNow,
        }, CancellationToken.None);
    }

    // ── Default tenant auth config ──────────────────────────────────────────────
    await configStore.SaveAsync(new TenantAuthConfig
    {
        TenantId = "demo",
        MfaPolicy = "optional",
        PasswordMinLength = 12,
        PasswordRequireUppercase = true,
        PasswordRequireNumber = true,
        PasswordRequireSpecial = false,
        LockoutThreshold = 5,
        LockoutDurationMinutes = 15,
        SessionIdleTimeoutMinutes = 30,
        SessionAbsoluteTimeoutHours = 12,
        UpdatedAt = clock.UtcNow,
    }, CancellationToken.None);

    // ── Sync demo tenant/agents/queues to Asterisk Realtime tables (best-effort)
    var syncService = app.Services.GetService<IRealtimeSyncService>();
    if (syncService is not null)
    {
        try
        {
            await syncService.ProvisionTenantAsync("demo");

            // Sync all agents
            foreach (var a in agentDefs)
                await syncService.SyncAgentAsync("demo", a.agentId, a.name, a.ext, a.ext);

            // Sync both queues
            await syncService.SyncQueueAsync("demo", "sales", new RealtimeQueueOptions
            {
                Timeout = 15, Wrapuptime = 10, Servicelevel = 20
            });
            await syncService.SyncQueueAsync("demo", "support", new RealtimeQueueOptions
            {
                Timeout = 20, Wrapuptime = 15, Servicelevel = 20
            });

            // Add members to queues
            foreach (var a in agentDefs.Where(x => x.skill == "sales"))
                await syncService.AddQueueMemberAsync("demo", "sales", a.agentId, a.name);
            foreach (var a in agentDefs.Where(x => x.skill == "support"))
                await syncService.AddQueueMemberAsync("demo", "support", a.agentId, a.name);

            Console.WriteLine("Asterisk Realtime: demo tenant provisioned.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Asterisk Realtime sync skipped: {ex.Message}");
        }
    }

    Console.WriteLine("Demo API keys seeded: demo-key-admin | demo-key-supervisor | demo-key-maria | demo-key-carlos | demo-key-ana | demo-key-pedro | demo-key-lucia | demo-key-demo");

    // ── RBAC seed: permissions, role templates, and enum migration ──────────
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
