using Asterisk.Platform.Api.Auth;
using Asterisk.Platform.Api.Endpoints;
using Asterisk.Platform.Api.Middleware;
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
using Microsoft.AspNetCore.Authentication;
using Asterisk.Sdk.Pro.Dialer.DependencyInjection;
using Asterisk.Sdk.Pro.Dialer.Storage.Postgres.DependencyInjection;
using Asterisk.Sdk.Pro.EventStore.Postgres.DependencyInjection;
using Asterisk.Sdk.Pro.CallAnalytics.Storage.Postgres.DependencyInjection;
using Asterisk.Sdk.Pro.Analytics.Storage.Postgres.DependencyInjection;
using Asterisk.Sdk.Pro.Licensing.DependencyInjection;
using Asterisk.Platform.Api.Services;
using Asterisk.Sdk.Pro.AgentAssist.Engine;
using Asterisk.Sdk.Pro.Routing.Skills;

var builder = WebApplication.CreateBuilder(args);

// ─── Core Platform Services ──────────────────────────────────────────────────

builder.Services.AddPlatformCore();
builder.Services.AddPlatformConversations();
builder.Services.AddPlatformChannels();
builder.Services.AddInboundRouting();
builder.Services.AddSwitchboard();
builder.Services.AddPlatformBot();
builder.Services.AddPlatformAudit();
builder.Services.AddPlatformMedia();

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

// ─── Pro.Dialer (Outbound Campaigns) ────────────────────────────────────────
var dialerConnectionString = builder.Configuration.GetConnectionString("Dialer") ?? builder.Configuration.GetConnectionString("Postgres") ?? "";
if (!string.IsNullOrEmpty(dialerConnectionString))
{
    builder.Services.UsePostgresDialerStorage(dialerConnectionString);
    builder.Services.AddProDialer(o => { });
    builder.Services.AddHostedService<CampaignMetricsPoller>();
}

// ─── Pro Analytics Stores (query only — no engine) ──────────────────────────
var analyticsConnectionString = builder.Configuration.GetConnectionString("Analytics") ?? dialerConnectionString;
if (!string.IsNullOrEmpty(analyticsConnectionString))
{
    builder.Services.UsePostgresEventStore(analyticsConnectionString);
    builder.Services.AddProCallAnalyticsPostgres(analyticsConnectionString);
    builder.Services.UsePostgresAnalyticsStore(analyticsConnectionString);
}

// ─── Recordings ─────────────────────────────────────────────────────────────
builder.Services.Configure<RecordingOptions>(o =>
{
    var path = builder.Configuration["Recordings:BasePath"];
    if (!string.IsNullOrEmpty(path))
        o.BasePath = path;
});

// ─── Authentication (API key) ─────────────────────────────────────────────────

builder.Services
    .AddAuthentication("ApiKey")
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", _ => { });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
    options.AddPolicy("SupervisorPlus", p => p.RequireRole("Admin", "Supervisor"));
    options.AddPolicy("Authenticated", p => p.RequireAuthenticatedUser());
});

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
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();

// ─── Endpoint mapping ────────────────────────────────────────────────────────

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
app.MapDncListEndpoints();
app.MapCallerIdPoolEndpoints();
app.MapHolidayCalendarEndpoints();
app.MapDialerSettingsEndpoints();
app.MapTrunkEndpoints();
app.MapOutboundRouteEndpoints();
app.MapRecordingEndpoints();
app.MapAnalyticsEndpoints();
app.MapQueueMetricsEndpoints();
app.MapBotEndpoints();
app.MapKnowledgeBaseEndpoints();
app.MapAgentAssistEndpoints();
app.MapSupervisorEndpoints();
app.MapSkillEndpoints();
app.MapAuditEndpoints();
app.MapSurveyEndpoints();

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

    Console.WriteLine("Demo API keys seeded: demo-key-admin | demo-key-supervisor | demo-key-agent");
}

app.Run();

// Expose Program for WebApplicationFactory in tests
public partial class Program { }
