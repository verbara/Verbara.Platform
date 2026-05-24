using Verbara.Platform.Identity.Redis.DependencyInjection;
using Verbara.Platform.Realtime.Auth;
using Verbara.Platform.Realtime.Clients;
using Verbara.Platform.Realtime.Contracts;
using Verbara.Platform.Realtime.Endpoints;
using Verbara.Platform.Realtime.Services;
using Verbara.Sdk.Cluster.Postgres.DependencyInjection;
using Verbara.Sdk.Pro.Cluster.DependencyInjection;
using Verbara.Sdk.Pro.Push;
using Verbara.Sdk.Pro.Push.SignalR.Authz;
using Verbara.Sdk.Pro.Push.SignalR.DependencyInjection;
using Verbara.Sdk.Pro.Push.SignalR.Hubs;
using Verbara.Sdk.Push.Authz;
using Verbara.Sdk.Push.Hosting;
using Verbara.Platform.Realtime.Authz;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Worker resilience parity with Platform.Api (ADR-0021): a crashing
// BackgroundService stops the host so K8s restarts the pod with a clear
// failure reason instead of silently swallowing the exception.
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});

builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);

// ─── Pro.Push event bus + cross-process backplane ────────────────────────────
// Local in-process bus. The Pro.Push.Redis backplane (registered below) bridges
// it to the same Redis pub/sub channels Platform.Api publishes on, so T27
// state-change events emitted in Platform.Api land in Realtime's bus and the
// PushToHubRelay (also below) fans them onto SignalR groups.
builder.Services.AddVerbaraPush();

// RBAC-aware subscription authorizer (replaces the SDK default Allow-All).
// Used by the SDK Push delivery filter pipeline when clients subscribe to
// topics over the Hub.
builder.Services.AddSingleton<ISubscriptionAuthorizer, RbacSubscriptionAuthorizer>();

var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddVerbaraProPushRedis(redisConnectionString);
}

// ─── Pro.Push.SignalR (Hub + presence CRDT + heartbeat services) ─────────────
builder.Services.AddVerbaraProPushSignalR(o =>
{
    var clusterNodeId = builder.Configuration["Asterisk:ClusterNodeId"];
    if (!string.IsNullOrEmpty(clusterNodeId))
        o.NodeId = clusterNodeId;
});

// SignalR Redis backplane — REQUIRED for multi-pod scaling so a broadcast in
// pod-A reaches a client connected to pod-B. No-op in single-pod / compose
// deployments, but adding it now keeps the K8s HPA path open.
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddSignalR().AddStackExchangeRedis(redisConnectionString, opts =>
    {
        opts.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("verbara:signalr");
    });
}

// ─── HTTP clients calling back into Platform.Api ─────────────────────────────
var platformApiBaseUrl = builder.Configuration["Services:PlatformApi:BaseUrl"]
    ?? "http://platform-api:5000";
var serviceKey = builder.Configuration["Services:ServiceKey"];

builder.Services.AddHttpClient("platform-api-internal", c =>
{
    c.BaseAddress = new Uri(platformApiBaseUrl.TrimEnd('/') + "/");
    if (!string.IsNullOrEmpty(serviceKey))
        c.DefaultRequestHeaders.Add("X-Service-Key", serviceKey);
});

// IAgentTenantResolver / IHubAuditSink are Pro.Push.SignalR abstractions the
// Hub consumes for cross-tenant subscription enforcement (ADR-0005) and
// hub-level audit (ADR-0005 §"Audit entry"). Pre-ADR-0022 both lived
// in-process inside Platform.Api; post-extraction Realtime calls back over
// HTTP+JSON to /api/v1/internal/agent-tenant and /api/v1/internal/hub-audit.
builder.Services.WithAgentTenantResolver<AgentTenantResolverClient>();
builder.Services.AddSingleton<IHubAuditSink, HubAuditSinkClient>();

// ─── Identity.Redis (shared JWT key pool with Platform.Api) ──────────────────
// Required for multi-replica deployments — tokens minted by Platform.Api are
// signed with a symmetric key drawn from the rotation pool stored in Redis
// (ADR-0012). Realtime fetches the same key for validation. Single-replica
// dev/compose deployments can omit the connection string; Realtime falls back
// to the in-memory IJwtKeyStore and ends up rejecting tokens from Platform.Api
// — so for any non-trivial setup the connection string MUST be set.
var identityRedis = builder.Configuration.GetConnectionString("IdentityRedis")
    ?? redisConnectionString;
if (!string.IsNullOrWhiteSpace(identityRedis))
{
    builder.Services.AddVerbaraPlatformIdentityRedis(o =>
    {
        o.ConnectionString = identityRedis;
        o.KeyPrefix = builder.Configuration["Identity:Redis:KeyPrefix"] ?? "verbara:platform:identity:";
    });
}
else
{
    builder.Services.AddSingleton<Verbara.Platform.Identity.Auth.Jwt.IJwtKeyStore,
        Verbara.Platform.Identity.Auth.Jwt.InMemoryJwtKeyStore>();
}

// ─── JWT authentication ──────────────────────────────────────────────────────
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        // Defer parameter construction until the service provider exists so
        // IJwtKeyStore is resolvable.
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters();
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                // Browser SignalR clients can't set the Authorization header on
                // the WebSocket handshake — accept ?access_token=... ONLY for
                // /hubs/* (mirrors Platform.Api's AUTH-002 fix path scoping).
                if (JwtValidationConfigurator.IsQueryTokenPathAllowed(ctx.Request.Path))
                {
                    var token = JwtValidationConfigurator.ExtractJwtQueryParam(ctx.Request);
                    if (token is not null)
                        ctx.Token = token;
                }
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IServiceProvider>((opts, sp) =>
    {
        opts.TokenValidationParameters = JwtValidationConfigurator.BuildValidationParameters(sp);
    });

// Hub method-level policies, copied verbatim from Verbara.Platform.Api Program.cs.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Supervisor", p => p.RequireRole("Supervisor", "Admin"));
    options.AddPolicy("Agent", p => p.RequireRole("Agent", "Supervisor", "Admin"));
    options.AddPolicy("PlatformAdmin", p => p.RequireRole("PlatformAdmin"));
});

// ─── Pro.Cluster leader election (ADR-0022 Phase A.5) ────────────────────────
// Per-resource leader election lets PushToHubRelay short-circuit fanout on
// non-leader pods, so only the leader publishes SignalR group broadcasts.
// The Redis backplane (above) then fans the leader's broadcasts to clients
// connected to every pod. Wiring sits between the auth + relay registrations
// because the relay constructor injects the keyed IClusterLeader.
//
// ConnectionStrings:Cluster falls back to :Postgres so SMB / single-DB
// deployments don't need to split the connection.
var clusterConnectionString =
    builder.Configuration.GetConnectionString("Cluster")
    ?? builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Cluster (or :Postgres fallback) is required for leader election.");

builder.Services.AddKeyedSingleton<NpgsqlDataSource>("Cluster", (_, _) =>
    NpgsqlDataSource.Create(clusterConnectionString));

builder.Services.AddPostgresDistributedLock(connectionStringName: "Cluster");

builder.Services.AddVerbaraCluster(opts =>
{
    opts.UsePostgresLockBackend(connectionStringName: "Cluster");
    opts.RegisterLeader(RealtimeLeaderResources.Fanout);
});

// ─── Push → Hub relay (leader-gated cross-pod fanout, ADR-0022 Phase A.5) ────
builder.Services.AddHostedService<PushToHubRelay>();

// ─── OpenTelemetry parity (optional) ─────────────────────────────────────────
// Realtime intentionally skips the heavyweight observability stack; Platform.Api
// remains the canonical telemetry surface. Future iteration may add a minimal
// SignalR connection-count gauge here.

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, RealtimeContractsJsonContext.Default);
});

var app = builder.Build();

// Phase A.5 Gap-1 (v2.4.3): ensure cluster_distributed_lock table exists at
// startup. AddVerbaraCluster + AddPostgresDistributedLock register DI for the
// lock primitive but do NOT auto-create the schema — the SDK ships
// MigrationRunner.EnsureSchemaAsync as an explicit opt-in. Without this call
// the leader-election TryAcquireAsync fails on first request with relation
// "cluster_distributed_lock" does not exist and the pod restart-loops. See
// docs/operations/phase-a5-smoke-test-2026-05-23.md §5 for the original gap.
using (var migrationScope = app.Services.CreateScope())
{
    var migrationDataSource = migrationScope.ServiceProvider
        .GetRequiredKeyedService<NpgsqlDataSource>("Cluster");
    var migrationLogger = migrationScope.ServiceProvider
        .GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>();
    await Verbara.Sdk.Cluster.Postgres.Migrations.MigrationRunner.EnsureSchemaAsync(
        migrationDataSource,
        migrationLogger,
        app.Lifetime.ApplicationStopping);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoint();
app.MapHub<PlatformHub>("/hubs/platform");

await app.RunAsync();
