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

builder.Services.AddAuthorization();

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

// ─── Dev seed: create a demo API key for local testing ───────────────────────
{
    using var scope = app.Services.CreateScope();
    var apiKeyStore = scope.ServiceProvider.GetRequiredService<IApiKeyStore>();
    var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();
    var agentStore = scope.ServiceProvider.GetRequiredService<IAgentStore>();
    var clock = scope.ServiceProvider.GetRequiredService<IClock>();

    var tenantId = new TenantId("demo");
    var rawKey = "demo-key-2026";
    var hashedKey = Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey)));

    await apiKeyStore.SaveAsync(new ApiKey
    {
        KeyId = EntityId.From("demo-key"),
        TenantId = tenantId,
        Name = "Demo Key",
        HashedKey = hashedKey,
        Scopes = ["*"],
        CreatedAt = clock.UtcNow,
    }, CancellationToken.None);

    var userId = EntityId.From("demo-user");
    await userStore.SaveAsync(new User
    {
        UserId = userId,
        TenantId = tenantId,
        Email = "admin@demo.local",
        DisplayName = "Demo Admin",
        Role = UserRole.Admin,
        Status = UserStatus.Active,
        CreatedAt = clock.UtcNow,
    }, CancellationToken.None);

    await agentStore.SaveAsync(new Agent
    {
        AgentId = EntityId.From("demo-agent"),
        TenantId = tenantId,
        UserId = userId,
        DisplayName = "Demo Admin",
        State = AgentState.Available,
        Skills = ["support"],
        CreatedAt = clock.UtcNow,
    }, CancellationToken.None);

    Console.WriteLine("🔑 Demo API key: demo-key-2026");
}

app.Run();

// Expose Program for WebApplicationFactory in tests
public partial class Program { }
