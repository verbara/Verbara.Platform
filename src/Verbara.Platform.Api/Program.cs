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
using Verbara.Platform.Flows;
using Verbara.Platform.Llm;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Storage.Postgres;
using Verbara.Platform.Audit;
using Verbara.Platform.Media;
using Verbara.Platform.Switchboard;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.DependencyInjection;
using Verbara.Platform.Identity.OidcTokenExchange;
using Verbara.Platform.Identity.Redis.DependencyInjection;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Sdk.Hosting;
using Verbara.Platform.KnowledgeBase;
using Verbara.Platform.Surveys;
using Verbara.Platform.Typification;
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
using Verbara.Sdk.Cluster.Postgres.DependencyInjection;
using Verbara.Platform.Channels.WebChat;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Pro.MultiTenant.DependencyInjection;
using Verbara.Sdk.Push.Hosting;
using Verbara.Sdk.Pro.Push;
using Verbara.Sdk.Pro.Push.SignalR.Bridges;
using Verbara.Sdk.Pro.Storage.Common.Retention;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Verbara.Platform.Api.Endpoints.Internal;
using Verbara.Sdk.OpenTelemetry;
using Verbara.Sdk.Pro.OpenTelemetry;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ─── Host-level worker resilience (ADR-0021) ─────────────────────────────────
// Verbara house-style: any BackgroundService that throws out of ExecuteAsync
// must cause the host to stop, so K8s/orchestrator restarts the pod with a
// clear "Last State Reason: Error" + the exception in --previous logs. Without
// this, the .NET default `Ignore` swallows the rethrow silently — the failure
// mode that produced D-LK's 21h-silent-QueueDistributionWorker death. Every
// hardened worker (this repo + Verbara.Sdk.Pro v2.4.1-pro) emits a Critical
// `WorkerCrash` log BEFORE rethrowing, so the stop is observable + attributable.
// See: docs/decisions/0021-stophost-on-worker-crash-house-style.md
//      docs/specs/2026-05-18-worker-resilience-pattern-hardening.md
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});

// ─── Verbara SDK Connection (Multi-Server + Sessions via Cluster) ───────────

builder.Services.AddVerbaraMultiServer();
builder.Services.AddVerbaraSessionsMultiServer();

// ─── Core Platform Services ──────────────────────────────────────────────────

// Verbara.Sdk.Push: in-process push event bus + delivery filter abstractions.
// MUST precede AddPlatformCore() so the IPushEventBus dependency is available
// for the PlatformEventBus DI ctor and the platform-specific delivery filter
// can override the SDK default.
builder.Services.AddVerbaraPush();

// ADR-0022 Phase A — The SignalR Hub itself (MapHub, PresenceTracker,
// PushToHubRelay) moved to Verbara.Platform.Realtime. Platform.Api KEEPS the
// T27 event bridges (WithClusterEventBridge / WithConversationBridge /
// WithAgentBridge) because they depend on SDK heavyweight types
// (ICallSessionManager, VerbaraServerPool, ClusterTransportBase) that live
// here. Bridges publish state-change events to IPushEventBus → Pro.Push
// Redis backplane → Realtime's local bus → PushToHubRelay → SignalR clients.
var pushBackplaneRedis = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(pushBackplaneRedis))
{
    builder.Services.AddVerbaraProPushRedis(pushBackplaneRedis);

    // v2.5.0 — supply the source-gen JsonSerializerContext that knows every
    // Verbara.Platform.Core push-event record. Without this,
    // RedisEventRelay.SerializePayload returns string.Empty (the Pro SDK
    // intentionally refuses reflection-based serialisation on the AOT-clean
    // path), the cross-node envelope's PayloadJson arrives empty on receiving
    // nodes, and the Verbara.Platform.Realtime RemoteEventDispatcher has
    // nothing to deserialise. Symptom in lab logs:
    //   "[PRESENCE] Remote PayloadJson empty
    //    (check PushProOptions.PayloadSerializerOptions)"
    // Pairs with the dispatcher registered in Verbara.Platform.Realtime
    // Program.cs — both sides MUST consume the same PlatformPushJsonContext
    // so a field-shape change is a compile-time error, not a runtime
    // silent message drop.
    builder.Services.Configure<Verbara.Sdk.Pro.Push.Options.PushProOptions>(o =>
    {
        o.PayloadSerializerOptions = new System.Text.Json.JsonSerializerOptions
        {
            TypeInfoResolver = Verbara.Platform.Core.Push.PlatformPushJsonContext.Default,
        };
    });
}

builder.Services
    .WithClusterEventBridge()
    .WithConversationBridge(opt => opt.DefaultTenantId = "default-tenant")
    .WithAgentBridge();

// Postgres-backed agent→tenant resolver consumed by /api/v1/internal/agent-tenant
// (which Realtime calls over HTTP+JSON to honour cross-tenant Hub subscription
// checks). Pre-ADR-0022 this implemented Pro.Push.SignalR's IAgentTenantResolver
// directly; the interface dependency was dropped along with the Pro.Push.SignalR
// PackageReference.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<Verbara.Platform.Api.Authz.CachedAgentTenantResolver>();
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
// Bridges WebChat inbound (which only persists messages) to the queue, mirroring the webhook
// path, so a visitor's chat reaches an agent. See WebChatInboundRouter.
builder.Services.AddSingleton<WebChatInboundRouter>();
builder.Services.AddPlatformBot();
// Real OpenAI-compatible LLM provider (P2a). Registered BEFORE AddPlatformFlows so it wins the
// Flows TryAddSingleton<ILlmProvider, DisabledLlmProvider> stub. No-op (stub stays) when the
// "Llm" config section is incomplete. Manual config read = AOT-safe (no reflection binding).
builder.Services.AddPlatformLlm(
    configure: o =>
    {
        var llm = builder.Configuration.GetSection("Llm");
        o.BaseUrl = llm["BaseUrl"];
        o.ApiKey = llm["ApiKey"];
        o.Model = llm["Model"];
        if (double.TryParse(llm["Temperature"], System.Globalization.CultureInfo.InvariantCulture, out var temperature))
            o.Temperature = temperature;
        if (int.TryParse(llm["MaxTokens"], System.Globalization.CultureInfo.InvariantCulture, out var maxTokens))
            o.MaxTokens = maxTokens;
        if (int.TryParse(llm["TimeoutSeconds"], System.Globalization.CultureInfo.InvariantCulture, out var timeoutSeconds))
            o.TimeoutSeconds = timeoutSeconds;
    },
    // P2c.2 (C4) — operator-managed (platform) LLM bound from "Llm:Platform". Manual key-by-key read
    // (NOT IConfiguration.Bind) keeps this AOT-safe (reflection-free), mirroring the BYO block above.
    // The operator ApiKey lives only here — never per-tenant, never serialized to any DTO.
    configurePlatform: p =>
    {
        var plat = builder.Configuration.GetSection("Llm:Platform");
        if (bool.TryParse(plat["Enabled"], out var enabled))
            p.Enabled = enabled;
        p.BaseUrl = plat["BaseUrl"];
        p.ApiKey = plat["ApiKey"];
        p.Model = plat["Model"] ?? p.Model;
        if (double.TryParse(plat["Temperature"], System.Globalization.CultureInfo.InvariantCulture, out var temperature))
            p.Temperature = temperature;
        if (int.TryParse(plat["MaxTokens"], System.Globalization.CultureInfo.InvariantCulture, out var maxTokens))
            p.MaxTokens = maxTokens;
        if (int.TryParse(plat["TimeoutSeconds"], System.Globalization.CultureInfo.InvariantCulture, out var timeoutSeconds))
            p.TimeoutSeconds = timeoutSeconds;
        if (long.TryParse(plat["CreditTokenRatio"], System.Globalization.CultureInfo.InvariantCulture, out var ratio))
            p.CreditTokenRatio = ratio;
        // typification-llm-inout-pricing — opt-in per-direction ratios; only set when present (null otherwise).
        if (long.TryParse(plat["InputCreditTokenRatio"], System.Globalization.CultureInfo.InvariantCulture, out var inRatio))
            p.InputCreditTokenRatio = inRatio;
        if (long.TryParse(plat["OutputCreditTokenRatio"], System.Globalization.CultureInfo.InvariantCulture, out var outRatio))
            p.OutputCreditTokenRatio = outRatio;
        // credit-ledger-cutover (ADR-0033) — two default-off kill-switches gating the ledger read seams.
        if (bool.TryParse(plat["LedgerEnforcementEnabled"], out var ledgerEnf))
            p.LedgerEnforcementEnabled = ledgerEnf;
        if (bool.TryParse(plat["LedgerInvoiceReadEnabled"], out var ledgerInv))
            p.LedgerInvoiceReadEnabled = ledgerInv;
        // credit-ledger-cutover (ADR-0033, task B7) — one-time current-period back-fill seed switch. Default off;
        // the operator sets it once for the cutover deploy, the hosted service runs once and logs completion.
        if (bool.TryParse(plat["RunLedgerBackfill"], out var runBf))
            p.RunLedgerBackfill = runBf;
    });
// Flow execution engine + node handlers (incl. collect_reason/set_variable for the P1
// typification capture paths). Ships a default-disabled ILlmProvider so the engine and AI
// handlers compose; AI nodes fail loudly until a real provider is configured (P2a wires one above).
builder.Services.AddPlatformFlows();
builder.Services.AddHostedService<Verbara.Platform.Api.Services.BotAnalyticsPersistenceService>();
builder.Services.AddPlatformAudit();
builder.Services.AddPlatformMedia();
builder.Services.AddPlatformKnowledgeBase();
builder.Services.AddPlatformSurveys();
builder.Services.AddPlatformTypification();
builder.Services.AddPlatformBilling();
// P2c.2 (C3/C4) — platform-managed Typification LLM credit meter (records AiAnalysis/Tokens usage via
// the Billing IMeteringService). Depends on IMeteringService (Billing) + IClock; registered here so
// the suggestion handler can resolve ITypificationCreditMeter.
builder.Services.AddSingleton<Verbara.Platform.Typification.Ai.ITypificationCreditMeter,
    Verbara.Platform.Api.Services.BillingTypificationCreditMeter>();
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

    // ADMIN-001 (PREPUB-2026-05-09): one-shot, idempotent encryption of any
    // legacy plaintext oidc_client_secret rows. Runs after the schema migrations
    // and after DataProtection is wired (registered later in this file). Safe
    // to leave registered — re-runs are no-ops once every row is encrypted.
    builder.Services.AddOidcClientSecretEncryptionMigrator();

    // P2c.1 (§6) — one-shot, idempotent seed of the appsettings global LLM key into the single
    // operational tenant's per-tenant tenant_llm_config row (the single-tenant / dev migration off
    // the retired shared global key). No-op unless the global key is set AND exactly one operational
    // tenant exists AND it has no config row yet. Runs after schema migrations + DataProtection.
    builder.Services.AddTenantLlmConfigSeedMigrator();

    // Override in-memory capacity with persistent version for restart recovery
    builder.Services.AddSingleton<IAgentCapacityService>(sp =>
        new PersistentAgentCapacityService(
            sp.GetRequiredService<IAgentCapacityResolver>(),
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

// ─── W6 capacity defaults provider ─────────────────────────────────────────────
// Supplies Queues' IAgentCapacityResolver with the per-tenant Max*Default columns,
// read through the (now cached) ITenantAuthConfigStore registered just above — so the
// DEFAULTS leg of the resolve inherits the auth hot-path cache for free. The AGENT leg
// (IAgentStore) is NOT auth-hot-path cached and issues a real SQL SELECT per resolve; W6
// collapses the capacity check to that single agent read (resolver-owned). Singleton.
builder.Services.AddSingleton<ICapacityDefaultsProvider, TenantAuthConfigCapacityDefaultsProvider>();

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
// ⚠️ Operator-supplied trust anchor override is optional. When unset, do NOT
// register a byte[] here — let Verbara.Sdk.Pro.Licensing's AddProLicensing()
// register LicenseTrustAnchor.OfficialPublicKey via its own TryAddSingleton.
if (!string.IsNullOrEmpty(publicKeyPath) && File.Exists(publicKeyPath))
{
    builder.Services.AddSingleton<byte[]>(File.ReadAllBytes(publicKeyPath));
}

// Pro v2.5.0-pro: EnforcementMode is GONE (ADR-0012). License-presence drives
// behaviour: file at LicenseFilePath valid → Pro enabled; missing/invalid/
// expired → Pro endpoints return HTTP 402 via LicenseGateMiddleware.
builder.Services.AddProLicensing(o =>
{
    o.LicenseFilePath = licensePath;
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
    // R5.5 Phase JWT Tier-1 hardening — expose verbara.platform.jwt meter
    // (jwt.key.cache_misses / jwt.key.stale_cache_fallbacks /
    // jwt.key.fail_closed) so we can prove the catch-when path is doing work
    // in prod, not just succeeding silently. Added 2026-05-25 after the v2.5.3
    // lab validation surfaced this gap: counters existed in JwtTokenService
    // but were never exported.
    .AddMeter("verbara.platform.jwt")
    // Typification P2b — expose verbara.platform.llm meter (llm.requests /
    // llm.errors / llm.request.latency / llm.tokens.in / llm.tokens.out).
    .AddMeter("verbara.platform.llm")
    // Typification P2b B2 — expose verbara.platform.typification.ai meter
    // (suggestion.made / suggestion.accepted / suggestion.overridden).
    .AddMeter(Verbara.Platform.Typification.Ai.TypificationAiMetrics.MeterName)
    // ADR-0026 Phase B — realtime.reconciliation.{tenants,memberships,sync_failures}
    // for Verbara queue_memberships → Asterisk Realtime drift observability.
    .AddMeter(Verbara.Platform.Api.Services.RealtimeReconciliationService.MeterName)
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
// CSAT webchat capture token verifier (csat-runner Phase B). Verifies the Platform-minted,
// session-signed v1.{payload}.{sig} responseToken. Secret is operator-supplied via
// Csat:WebChatTokenSecret; when unset a stable per-deployment key is derived so the
// verifier still rejects tampered/expired tokens (the minter ships with the WebChat embed).
builder.Services.AddSingleton<Verbara.Platform.Api.Services.ICsatWebChatTokenVerifier>(_ =>
{
    var configured = builder.Configuration["Csat:WebChatTokenSecret"];
    var secret = !string.IsNullOrEmpty(configured)
        ? System.Text.Encoding.UTF8.GetBytes(configured)
        : System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("verbara.csat.webchat.token:" + (licensePath ?? "default")));
    return new Verbara.Platform.Api.Services.HmacCsatWebChatTokenVerifier(secret);
});
// CSAT voice-leg capture token verifier (csat-completion, Platform/ADR-0020). Verifies the
// Platform-minted v1.{payload}.{sig} responseToken the survey-IVR leg submits with its DTMF rating.
// Mirrors the webchat token pattern; the secret is shared (Csat:WebChatTokenSecret) so a single CSAT
// signing key covers both channels, with the same stable per-deployment fallback.
builder.Services.AddSingleton<Verbara.Platform.Api.Services.ICsatVoiceTokenVerifier>(_ =>
{
    var configured = builder.Configuration["Csat:WebChatTokenSecret"];
    var secret = !string.IsNullOrEmpty(configured)
        ? System.Text.Encoding.UTF8.GetBytes(configured)
        : System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("verbara.csat.webchat.token:" + (licensePath ?? "default")));
    return new Verbara.Platform.Api.Services.HmacCsatVoiceTokenVerifier(secret);
});
// CSAT template provider (csat-runner Phase E). Platform's in-process implementation of
// Pro's Verbara.Sdk.Pro.CsatRunner.ICsatTemplateProvider contract — Pro's channel adapters
// resolve locale-templated prompts via DI (NOT an API call; Platform/ADR-0020 boundary).
// Backed by ICsatTemplateStore over csat_templates; fallback chain tenant-locale →
// tenant-default-locale → global-default-locale → global-default-en-US.
builder.Services.AddSingleton<Verbara.Sdk.Pro.CsatRunner.Contracts.ICsatTemplateProvider,
    Verbara.Platform.Api.Services.CsatTemplateProvider>();

// ─── CSAT Runner: Pro engine hosting + the 4 remaining dispatch/trigger seams ─────
// csat-runner Phase E2 (tasks 5b.1–5b.5). Pro (upstream) can't reference Platform
// (downstream), so its CSAT orchestrator (an IHostedService) consumes 5 Pro-owned seams
// via dependency inversion; Platform implements them and hosts the engine. AddProCsatRunner
// registers the orchestrator + the 3 Pro channel adapters + the license/sampling/metrics
// foundation; the orchestrator self-gates at runtime on LicenseFeature.CsatRunner via the
// Pro ILicenseGuard (no composition-time gate needed). The 5 seams (4 here + the
// ICsatTemplateProvider above) MUST all resolve or the orchestrator fails to construct.

// 5b.1 — webchat: bridges to IConversationService.SendMessageAsync (csat_requested system message).
builder.Services.AddSingleton<Verbara.Sdk.Pro.CsatRunner.Contracts.ICsatConversationSignal,
    Verbara.Platform.Api.Services.CsatConversationSignalAdapter>();

// 5b.2 — email: bridges to IEmailService.SendAsync; Reply-To rides EmailMessage.ReplyToAddress.
builder.Services.AddSingleton<Verbara.Sdk.Pro.CsatRunner.Contracts.ICsatEmailDispatcher,
    Verbara.Platform.Api.Services.CsatEmailDispatcherAdapter>();

// 5b.3 — sms: bridges to ISmsProvider.SendAsync + writes the csat_pending_dispatches row the
// Phase-D CsatSmsCorrelator consumes. ISmsProvider (Twilio) + NpgsqlDataSource are resolved
// optionally so the adapter constructs under the Testing/in-memory profile (fails only at
// dispatch time when SMS is genuinely unconfigured). The outbound sender number comes from
// Twilio config (there is no AddSms/SmsOptions binding in the Api host).
builder.Services.AddSingleton<Verbara.Sdk.Pro.CsatRunner.Contracts.ICsatSmsDispatcher>(sp =>
    new Verbara.Platform.Api.Services.CsatSmsDispatcherAdapter(
        sp.GetRequiredService<Verbara.Platform.Conversations.IConversationStore>(),
        sp.GetRequiredService<Verbara.Platform.Queues.IQueueStore>(),
        sp.GetRequiredService<Verbara.Platform.Surveys.ISurveyStore>(),
        builder.Configuration["Twilio:FromNumber"] ?? builder.Configuration["Sms:DefaultFromNumber"] ?? string.Empty,
        sp.GetService<Verbara.Platform.Channels.Sms.ISmsProvider>(),
        sp.GetService<NpgsqlDataSource>(),
        sp.GetService<TimeProvider>()));

// 5b.4 — conversation-end trigger source (ICsatConversationEndSource) driven from the
// ConversationStateChangedEvent → Closed transition on PlatformEventBus. Singleton exposed via
// the Pro seam AND run as a hosted service (it subscribes to the end stream and pushes signals).
builder.Services.AddSingleton<Verbara.Platform.Api.Services.CsatConversationEndSource>();
builder.Services.AddSingleton<Verbara.Sdk.Pro.CsatRunner.Contracts.ICsatConversationEndSource>(sp =>
    sp.GetRequiredService<Verbara.Platform.Api.Services.CsatConversationEndSource>());
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Verbara.Platform.Api.Services.CsatConversationEndSource>());

// 5b.6 — voice CSAT seams (csat-completion, Platform/ADR-0020). AddProCsatRunner now wires the Pro
// voice adapter unconditionally, so its host-supplied seams MUST resolve: the capture sink (lands the
// collected DTMF rating on the Surveys capture path), the DTMF source (adapts the AMI DTMFEnd stream),
// the AMI connection (the primary server's connection), and a SpeechSynthesizer (the TtsPromptCache TTS
// seam — a real provider when configured, else the offline SilenceSpeechSynthesizer). These are
// registered BEFORE AddProCsatRunner so the voice adapter + TtsPromptCache construct.
builder.Services.AddSingleton<Verbara.Sdk.Pro.CsatRunner.Contracts.ICsatVoiceCaptureSink,
    Verbara.Platform.Api.Services.CsatVoiceCaptureSinkAdapter>();
builder.Services.AddSingleton<Verbara.Sdk.Pro.CsatRunner.Adapters.Voice.IDtmfSource,
    Verbara.Platform.Api.Services.AmiDtmfSource>();
// IAmiConnection: the voice adapter AMI-BlindTransfers the caller leg via the primary server's
// connection. Deferred to first USE (DeferredPrimaryAmiConnection), NOT resolved at boot: the
// CsatRunnerOrchestrator constructs the voice adapter (and thus resolves this) during Host.StartAsync,
// so a throwing factory here crashed every headless / no-telephony boot (CI OpenAPI-export capture,
// minimal deploys). The wrapper looks up GetServer("primary") on each member access and throws the
// descriptive InvalidOperationException only when a dispatch genuinely needs AMI and none is configured
// — fail-at-use, mirroring AmiDtmfSource's fail-closed pool access. The adapter only runs on the
// AMI-owner pod with a live primary server, so deferring the failure is semantically correct.
builder.Services.TryAddSingleton<Verbara.Sdk.IAmiConnection>(sp =>
    new Verbara.Platform.Api.Services.DeferredPrimaryAmiConnection(
        sp.GetRequiredService<Verbara.Sdk.Live.Server.VerbaraServerPool>()));
// SpeechSynthesizer: default to the offline Silence synthesizer so a dev / single-host deployment is
// self-contained. TryAdd lets an operator register a real SDK TTS provider (Azure / ElevenLabs / …)
// earlier in the composition root to supersede it.
builder.Services.TryAddSingleton<Verbara.Sdk.VoiceAi.SpeechSynthesizer,
    Verbara.Platform.Api.Services.SilenceSpeechSynthesizer>();

// 5b.5 — host Pro's CSAT engine. WithEmail configures the tokenized Reply-To (domain + HMAC
// signing secret shared with the Phase-C inbound reply parser); WithWebChat pins the
// csat_requested system-message type; WithVoice pins the shared survey-IVR dialplan context/exten
// (matching Platform's [survey-ivr] context + VoiceCallControlService.SurveyIvr handoff). The
// orchestrator + 4 channel adapters + hosted service register here.
Verbara.Sdk.Pro.CsatRunner.DependencyInjection.CsatRunnerServiceCollectionExtensions.AddProCsatRunner(
    builder.Services,
    csat => csat
        .WithEmail(email =>
        {
            email.ReplyToDomain = builder.Configuration["Csat:Email:ReplyToDomain"] ?? "csat.verbara.local";
            email.TokenSigningSecret = builder.Configuration["Csat:Email:TokenSigningSecret"]
                ?? builder.Configuration["Csat:WebChatTokenSecret"]
                ?? "verbara.csat.email.reply-token." + (licensePath ?? "default");
        })
        .WithVoice(voice => voice.SurveyIvrContext = "survey-ivr")
        .WithWebChat(web => web.SystemMessageType = "csat_requested"));

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
    if (int.TryParse(s["OverageGraceDays"], out var ogd)) o.OverageGraceDays = ogd;
    if (int.TryParse(s["PaymentTermDays"], out var ptd)) o.PaymentTermDays = ptd;
});
builder.Services.AddHostedService<DunningService>();
builder.Services.AddHostedService<OverageInvoiceIssuanceWorker>();
builder.Services.AddHostedService<CreditGrantMintWorker>();
// credit-ledger-lots (ADR-0033 (c2), Group 4) — hourly lot-expiry reclaim sweeper: reclaims expired
// non-empty lots idempotently (subscription no-carryover + promo expiry), tagging the offset the lot's own source.
builder.Services.AddHostedService<CreditLotExpiryReclaimWorker>();
// credit-ledger-cutover (ADR-0033, task B7) — one-time current-period back-fill seed, gated by
// Llm:Platform:RunLedgerBackfill (default off); runs once and logs completion, idempotent on re-run.
builder.Services.AddHostedService<CreditLedgerBackfillService>();

// ─── ACD Distribution ───────────────────────────────────────────────────────
builder.Services.Configure<DistributionOptions>(o =>
{
    var s = builder.Configuration.GetSection("Distribution");
    if (int.TryParse(s["PollIntervalMs"], out var pim)) o.PollIntervalMs = pim;
    if (int.TryParse(s["OfferTimeoutSeconds"], out var ots)) o.OfferTimeoutSeconds = ots;
    if (int.TryParse(s["DefaultQueueTimeoutSeconds"], out var dqts)) o.DefaultQueueTimeoutSeconds = dqts;
    if (int.TryParse(s["DefaultWrapUpTimeoutSeconds"], out var dwuts)) o.DefaultWrapUpTimeoutSeconds = dwuts;
    if (int.TryParse(s["MaxConversationsPerCycle"], out var mcpc)) o.MaxConversationsPerCycle = mcpc;
    // Autonomous AI disposition (ADR-0034) — global breaker default OFF (byte-identical close path);
    // cap + correction window govern blast radius and the audit retention floor. Kill-switches, not
    // production cadence (see DistributionOptions XML docs).
    if (bool.TryParse(s["AutonomousDispositionEnabled"], out var ade)) o.AutonomousDispositionEnabled = ade;
    if (int.TryParse(s["AutonomousDispositionPerCycleCap"], out var adpcc)) o.AutonomousDispositionPerCycleCap = adpcc;
    if (int.TryParse(s["AutonomousCorrectionWindowDays"], out var acwd)) o.AutonomousCorrectionWindowDays = acwd;
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
// Per ADR-0003 + ADR-0022 Phase B: Postgres-backed via NpgsqlXmlRepository (replaces
// the legacy EF Core PersistKeysToDbContext path so the host stays AOT-clean). Test
// environments fall back to ephemeral keys so endpoint tests don't require a live
// Postgres connection just to exercise non-encryption paths.
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
    // Resolve through the shared NpgsqlDataSource cache (ADR-0015 Phase 2) so
    // DataProtection joins the host pool instead of opening its own. Pool
    // defaults were applied to coreConnectionString upstream via
    // ConnectionStringDefaults.ApplyPoolDefaults.
    var dataProtectionDataSource = ResolveDataSource(coreConnectionString)
        ?? throw new InvalidOperationException(
            "ResolveDataSource returned null for the DataProtection connection string — " +
            "this should be unreachable given the non-empty check above.");
    builder.Services.AddPlatformDataProtection(opt =>
    {
        opt.ApplicationName = "Verbara.Platform";
        opt.UsePostgres(dataProtectionDataSource);
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
        sp.GetRequiredService<IJtiRevocationCache>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JwtTokenService>>(),
        sp.GetService<System.Diagnostics.Metrics.IMeterFactory>()));

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
        sp.GetRequiredService<IJtiRevocationCache>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JwtTokenService>>(),
        sp.GetService<System.Diagnostics.Metrics.IMeterFactory>()));
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

// W3 server-side agent liveness — default to the in-memory presence store
// (single-instance safe). When ConnectionStrings:IdentityRedis is set, the
// registration below is swapped to RedisAgentLivenessStore so presence keys
// are shared across API instances.
builder.Services.AddSingleton<Verbara.Platform.Queues.Services.IAgentLivenessStore,
    Verbara.Platform.Storage.InMemory.InMemoryAgentLivenessStore>();

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

    // W3 — swap the in-memory agent-liveness store for the Redis-backed one so
    // presence keys (and the liveness reaper's view) are shared across replicas.
    // The shared IConnectionMultiplexer is registered (TryAddSingleton) by
    // AddVerbaraPlatformIdentityRedis above.
    var presencePrefix = builder.Configuration["Identity:Redis:KeyPrefix"] ?? "asterisk:identity:";
    builder.Services.RemoveAll<Verbara.Platform.Queues.Services.IAgentLivenessStore>();
    builder.Services.AddSingleton<Verbara.Platform.Queues.Services.IAgentLivenessStore>(sp =>
        new RedisAgentLivenessStore(sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(), presencePrefix));
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
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    Verbara.Platform.Api.Services.RealtimeReconciliationService.ResiliencePolicyKey,
    (_, _) => BuildDefaultWorkerPolicy());

// DB-heavy workers (long batch DELETEs / bulk inserts)
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    RetentionPurgeService.ResiliencePolicyKey, (_, _) => BuildDbHeavyWorkerPolicy());
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    AuditRetentionService.ResiliencePolicyKey, (_, _) => BuildDbHeavyWorkerPolicy());
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    BotAnalyticsPersistenceService.ResiliencePolicyKey, (_, _) => BuildDbHeavyWorkerPolicy());

// Hourly-cadence workers (dunning + overage-invoice issuance share the CheckIntervalHours cadence)
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    Verbara.Platform.Billing.DunningService.ResiliencePolicyKey,
    (_, _) => BuildHourlyWorkerPolicy());
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    Verbara.Platform.Billing.OverageInvoiceIssuanceWorker.ResiliencePolicyKey,
    (_, _) => BuildHourlyWorkerPolicy());
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    Verbara.Platform.Billing.CreditGrantMintWorker.ResiliencePolicyKey,
    (_, _) => BuildHourlyWorkerPolicy());
builder.Services.AddKeyedSingleton<Verbara.Sdk.Resilience.ResiliencePolicy>(
    Verbara.Platform.Billing.CreditLotExpiryReclaimWorker.ResiliencePolicyKey,
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
    var clusterDataSource = ResolveDataSource(clusterConn)!;
    builder.Services.UsePostgresClusterTransport(clusterDataSource);

    // ─── Phase 2.2 voice inbound — per-resource leader election ──────────────────
    // StasisInboundConsumer opens an ARI WebSocket, and Asterisk delivers a Stasis
    // app to exactly ONE socket — so only the elected leader pod connects + consumes
    // (a physical call can't be re-emitted, so this gates the *connection*, stronger
    // than the relay's per-event short-circuit). Mirrors the Realtime microservice
    // wiring (ADR-0022 Phase A.5): a keyed "Cluster" NpgsqlDataSource (the SAME shared
    // instance as the transport — no extra pool, ADR-0015) backs AddPostgresDistributedLock,
    // and the builder AddVerbaraCluster overload registers the keyed IClusterLeader.
    // This coexists with the ClusterOptions AddVerbaraCluster above (independent
    // overloads). Gated additionally on Asterisk:Ari being configured so that ONLY an
    // ARI-capable pod can ever win the voice-inbound lease — making "a pod that can't
    // connect Asterisk never holds the lease while a capable pod stays follower" a
    // structural invariant rather than relying on Helm env homogeneity.
    // Phase 3B.0 adds a SECOND voice lease: voice:ami:owner gates the single pod that
    // EMITS AMI-driven side-effects (VoiceConversationBridge — tracked voice Conversation,
    // capacity, agent Busy/ACW, tenant stamping). Every pod's CallSessionManager observes the
    // same broadcast AMI stream (warm standby), but only the leader acts, so side-effects
    // happen exactly once cluster-wide with no cold-start gap on failover. It is gated on
    // Asterisk:Ami (Hostname) — NOT Ari — because AMI capability ≠ ARI capability; a pod must
    // be AMI-capable to own the AMI side-effect plane. Both leases share the one lock backend +
    // keyed "Cluster" datasource (one pool, ADR-0015): a single AddVerbaraCluster with two
    // RegisterLeader calls (the Pro builder materializes each lease independently).
    var voiceInboundEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["Asterisk:Ari:BaseUrl"]);
    var voiceAmiEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["Asterisk:Ami:Hostname"]);

    // W3 — the cluster/lock/leader registration is lifted to the clusterConn level (out of the
    // voice-only sub-block below) because the agent-liveness lease must run on EVERY clustered
    // deployment, not just voice-capable ones. The one shared lock backend + keyed "Cluster"
    // datasource (one pool, ADR-0015) materializes each lease independently: the liveness lease
    // ALWAYS, the two voice leases only when their respective Asterisk capability is configured.
    builder.Services.AddKeyedSingleton<NpgsqlDataSource>("Cluster", (_, _) => clusterDataSource);
    builder.Services.AddPostgresDistributedLock(connectionStringName: "Cluster");
    builder.Services.AddVerbaraCluster(opts =>
    {
        opts.UsePostgresLockBackend(connectionStringName: "Cluster");
        opts.RegisterLeader(Verbara.Platform.Api.Services.AgentLivenessLeaderResources.Sweep);   // W3 — always
        opts.RegisterLeader(Verbara.Platform.Api.Services.PendingPauseLeaderResources.Sweep);    // W4 — always
        opts.RegisterLeader(Verbara.Platform.Api.Services.WorkFailoverLeaderResources.Sweep);    // W5 — always
        if (voiceInboundEnabled)
            opts.RegisterLeader(Verbara.Platform.Api.Services.VoiceLeaderResources.Inbound);
        if (voiceAmiEnabled)
        {
            opts.RegisterLeader(Verbara.Platform.Api.Services.VoiceLeaderResources.AmiOwner);
            opts.RegisterLeader(Verbara.Platform.Api.Services.CallbackRescueLeaderResources.Sweep);    // W5b — voice-AMI only
        }
    });
    if (voiceInboundEnabled || voiceAmiEnabled)
    {
        if (voiceInboundEnabled)
            builder.Services.AddHostedService<Verbara.Platform.Api.Services.StasisInboundConsumer>();
        if (voiceAmiEnabled)
        {
            builder.Services.AddHostedService<Verbara.Platform.Api.Services.VoiceConversationBridge>();
            // Blind transfer (3B.2c queue/agent; 3B.2d external) — leader-gated AMI Redirect; request-driven.
            // External transfer reuses the outbound route→trunk + tenant caller-ID resolution, so the route
            // resolver is optional (GetService — only with the full dialer stack) + the default trunk config.
            builder.Services.AddSingleton<Verbara.Platform.Api.Services.IVoiceCallControlService>(sp =>
                new Verbara.Platform.Api.Services.VoiceCallControlService(
                    sp.GetRequiredService<Verbara.Platform.Conversations.IConversationStore>(),
                    sp.GetRequiredService<Verbara.Platform.Queues.IQueueStore>(),
                    sp.GetRequiredService<Verbara.Platform.Queues.IAgentStore>(),
                    sp.GetRequiredService<Verbara.Sdk.Live.Server.VerbaraServerPool>(),
                    sp.GetService<Verbara.Sdk.Pro.Dialer.Routing.OutboundRouteResolverBase>(),
                    sp.GetRequiredService<Verbara.Sdk.Pro.MultiTenant.ITenantStore>(),
                    sp.GetRequiredKeyedService<Verbara.Sdk.Pro.Cluster.Leadership.IClusterLeader>(
                        Verbara.Platform.Api.Services.VoiceLeaderResources.AmiOwner),
                    builder.Configuration["Asterisk:Outbound:DefaultTrunk"],
                    sp.GetRequiredService<ILogger<Verbara.Platform.Api.Services.VoiceCallControlService>>()));
            // Trunk connectivity test (P2): leader-gated AMI `pjsip show ...` diagnostic run server-side
            // (replaces the operator SSHing into Asterisk, manual §3.3). TrunkStoreBase resolves lazily —
            // its registration is below, but this factory only touches it at first request.
            builder.Services.AddSingleton<Verbara.Platform.Api.Services.ITrunkConnectivityTester>(sp =>
                new Verbara.Platform.Api.Services.TrunkConnectivityTester(
                    sp.GetRequiredService<TrunkStoreBase>(),
                    sp.GetRequiredService<Verbara.Sdk.Live.Server.VerbaraServerPool>(),
                    sp.GetRequiredKeyedService<Verbara.Sdk.Pro.Cluster.Leadership.IClusterLeader>(
                        Verbara.Platform.Api.Services.VoiceLeaderResources.AmiOwner),
                    sp.GetRequiredService<ILogger<Verbara.Platform.Api.Services.TrunkConnectivityTester>>()));
            // Outbound click-to-dial (3B.2d): reuse the Pro Dialer originate executor (circuit-breaker +
            // trunk-health). It is NOT registered by AddProDialer (consumed via GetService there), and its
            // trunk-health / post-call / time-provider deps are optional, so register it via a factory that
            // resolves those with GetService — works for voice-only tenants without the full dialer stack.
            builder.Services.TryAddSingleton<Verbara.Sdk.Pro.Dialer.Execution.OriginateExecutorBase>(sp =>
                new Verbara.Sdk.Pro.Dialer.Execution.DefaultOriginateExecutor(
                    sp.GetRequiredService<Verbara.Sdk.Live.Server.VerbaraServerPool>(),
                    sp.GetRequiredService<ILogger<Verbara.Sdk.Pro.Dialer.Execution.DefaultOriginateExecutor>>(),
                    sp.GetService<Verbara.Sdk.Pro.Dialer.Health.ITrunkHealthProvider>(),
                    sp.GetService<Verbara.Sdk.Pro.Dialer.PostCall.PostCallHandlerBase>(),
                    sp.GetService<TimeProvider>()));
            // The outbound route + DNC resolvers register only with the full dialer Postgres storage, so
            // they are optional here (GetService): a voice-only tenant fails DNC open + uses the default trunk.
            builder.Services.AddSingleton<Verbara.Platform.Api.Services.IAgentOutboundDialService>(sp =>
                new Verbara.Platform.Api.Services.AgentOutboundDialService(
                    sp.GetRequiredService<Verbara.Platform.Conversations.IConversationStore>(),
                    sp.GetRequiredService<Verbara.Platform.Conversations.Services.IContactIdentityResolver>(),
                    sp.GetRequiredService<Verbara.Platform.Queues.IAgentStore>(),
                    sp.GetRequiredService<Verbara.Sdk.Pro.MultiTenant.ITenantStore>(),
                    sp.GetRequiredService<Verbara.Sdk.Pro.Dialer.Execution.OriginateExecutorBase>(),
                    sp.GetService<Verbara.Sdk.Pro.Dialer.Routing.OutboundRouteResolverBase>(),
                    sp.GetService<Verbara.Sdk.Pro.Dialer.Compliance.DncCheckerBase>(),
                    sp.GetRequiredKeyedService<Verbara.Sdk.Pro.Cluster.Leadership.IClusterLeader>(
                        Verbara.Platform.Api.Services.VoiceLeaderResources.AmiOwner),
                    sp.GetRequiredService<Verbara.Platform.Core.IClock>(),
                    builder.Configuration["Asterisk:Outbound:DefaultTrunk"],
                    sp.GetRequiredService<ILogger<Verbara.Platform.Api.Services.AgentOutboundDialService>>()));
            // W5b voice caller-rescue: originates a callback to a dropped customer into their origin queue
            // at the front (QUEUE_PRIO). Reuses the same originate executor + optional route resolver +
            // default trunk as click-to-dial. NOT leader-gated here — the leader-gated CallbackRescueWorker
            // (task A6) holds the AMI-owner lease and invokes it.
            builder.Services.AddSingleton<Verbara.Platform.Api.Services.ICallbackOriginator>(sp =>
                new Verbara.Platform.Api.Services.CallbackOriginator(
                    sp.GetRequiredService<Verbara.Platform.Conversations.IConversationStore>(),
                    sp.GetRequiredService<Verbara.Platform.Queues.IQueueStore>(),
                    sp.GetRequiredService<Verbara.Sdk.Pro.MultiTenant.ITenantStore>(),
                    sp.GetRequiredService<Verbara.Platform.Conversations.Services.IContactIdentityResolver>(),
                    sp.GetRequiredService<Verbara.Sdk.Pro.Dialer.Execution.OriginateExecutorBase>(),
                    sp.GetService<Verbara.Sdk.Pro.Dialer.Routing.OutboundRouteResolverBase>(),
                    sp.GetRequiredService<Verbara.Platform.Core.IClock>(),
                    builder.Configuration["Asterisk:Outbound:DefaultTrunk"],
                    sp.GetRequiredService<ILogger<Verbara.Platform.Api.Services.CallbackOriginator>>()));
            // W5b — leader-gated voice callback-rescue worker. Registered ONLY on a voice-AMI pod
            // (its ICallbackOriginator dependency exists only here, and voice itself requires the
            // cluster connection): the leader gate inside SweepOnceAsync ensures exactly one pod
            // originates rescue callbacks cluster-wide. Mirrors the W5 worker, but voice-gated.
            builder.Services.AddHostedService<Verbara.Platform.Api.Services.CallbackRescueWorker>();
        }
    }
}
else
{
    // W3 — single-node (no cluster conn): always-leader stub for the agent-liveness
    // sweep resource so the reaper still runs on a single replica. Clustered
    // deployments take the Postgres-lease-backed leader registered above instead.
    builder.Services.AddKeyedSingleton<Verbara.Sdk.Pro.Cluster.Leadership.IClusterLeader>(
        Verbara.Platform.Api.Services.AgentLivenessLeaderResources.Sweep,
        (_, _) => new Verbara.Platform.Api.Services.AlwaysLeader(
            Verbara.Platform.Api.Services.AgentLivenessLeaderResources.Sweep));
    // W4 — single-node always-leader stub for the deferred-pause drain sweep resource
    // so the drain worker still runs on a single replica (same rationale as W3 above).
    builder.Services.AddKeyedSingleton<Verbara.Sdk.Pro.Cluster.Leadership.IClusterLeader>(
        Verbara.Platform.Api.Services.PendingPauseLeaderResources.Sweep,
        (_, _) => new Verbara.Platform.Api.Services.AlwaysLeader(
            Verbara.Platform.Api.Services.PendingPauseLeaderResources.Sweep));
    // W5 — single-node always-leader stub for the work-failover sweep resource so the
    // failover worker still runs on a single replica (same rationale as W3/W4 above).
    builder.Services.AddKeyedSingleton<Verbara.Sdk.Pro.Cluster.Leadership.IClusterLeader>(
        Verbara.Platform.Api.Services.WorkFailoverLeaderResources.Sweep,
        (_, _) => new Verbara.Platform.Api.Services.AlwaysLeader(
            Verbara.Platform.Api.Services.WorkFailoverLeaderResources.Sweep));
}

// W3 — leader-gated agent-liveness reaper. Runs on every pod (both cluster + single-node
// branches register the keyed IClusterLeader for AgentLivenessLeaderResources.Sweep); the
// leader gate inside SweepOnceAsync ensures exactly one pod reconciles cluster-wide. The
// keyed leader is resolved lazily at host start, so registering here (regardless of branch)
// is safe.
builder.Services.AddHostedService<Verbara.Platform.Api.Services.AgentLivenessReaper>();

// W4 — leader-gated deferred-pause drain worker. Runs on every pod (both cluster +
// single-node branches register the keyed IClusterLeader for
// PendingPauseLeaderResources.Sweep); the leader gate inside SweepOnceAsync ensures
// exactly one pod applies pending pauses cluster-wide. Mirrors the W3 reaper wiring above.
builder.Services.AddHostedService<Verbara.Platform.Api.Services.PendingPauseDrainWorker>();

// W5 — leader-gated work-failover worker. Runs on every pod (both cluster + single-node
// branches register the keyed IClusterLeader for WorkFailoverLeaderResources.Sweep); the
// leader gate inside SweepOnceAsync ensures exactly one pod re-queues orphaned
// conversations cluster-wide. Mirrors the W3/W4 worker wiring above.
builder.Services.AddHostedService<Verbara.Platform.Api.Services.WorkFailoverWorker>();

// W5b — the voice callback-rescue worker is NOT registered here (unlike W3/W4/W5). It depends
// on ICallbackOriginator, which exists only on a voice-AMI pod, so it is registered inside the
// voiceAmiEnabled block of the cluster branch above (next to ICallbackOriginator + the
// CallbackRescueLeaderResources.Sweep lease). Voice requires the cluster connection, so there is
// no single-node path for it.

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

// ADR-0026 Phase B — convergent reconciler for queue_memberships → Asterisk
// Realtime store. Catches up any AddQueueMemberAsync calls swallowed by the
// best-effort try/catch in QueueMembersEndpoints + AdminEndpoints. Uses
// RealtimeOptions.ReconcilerIntervalSeconds (default 60s).
builder.Services.AddHostedService<Verbara.Platform.Api.Services.RealtimeReconciliationService>();

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

    // c1 (credit-ledger-topups) — top-up mint double-lock: host/partner-tenant gate + the seeded
    // billing:credits:grant permission, enforced against both management-key scopes and user JWT
    // permissions (minting AI credits is money creation, so the permission must be a real gate).
    options.AddPolicy(
        Verbara.Platform.Api.Endpoints.CreditLedgerEndpoints.GrantPolicy,
        p => p.AddRequirements(new PlatformAdminRequirement("billing:credits:grant")));

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

    // Plan 32C PlatformHub method-level policies (Supervisor/Agent/PlatformAdmin)
    // moved to Verbara.Platform.Realtime per ADR-0022 along with the Hub itself.
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
// ADR-0022 Phase A — Presence health checks moved to Verbara.Platform.Realtime
// along with the Pro.Push.SignalR services that host them. RetentionService
// remains in Platform.Api.
builder.Services
    .PromoteHostedServiceToSingleton<RetentionService>();

var healthBuilder = builder.Services.AddHealthChecks()
    .AddCheck<Verbara.Platform.Api.Health.BackgroundServiceHealthCheck>("services", tags: ["ready"])
    .AddCheck<Verbara.Platform.Api.Health.AsteriskAmiHealthCheck>("asterisk", tags: ["ready"])
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
    // Cross-service DTOs shared with Verbara.Platform.Realtime (ADR-0022). Adding the
    // resolver lets the /api/v1/internal endpoints serialize/deserialize without
    // tripping the AOT analyzer or registering each DTO in ApiJsonContext.
    options.SerializerOptions.TypeInfoResolverChain.Insert(1,
        Verbara.Platform.Realtime.Contracts.RealtimeContractsJsonContext.Default);
    // ADR-0022 Phase C — the non-generic JsonStringEnumConverter was a runtime
    // fallback for enums that hadn't been registered in ApiJsonContext. It
    // trips IL3050 under Native AOT. Every enum the API surface emits is
    // declared in ApiJsonContext's [JsonSerializable] manifest; if a new enum
    // type is added without a context entry, serialization will throw at
    // call-time instead of silently using a reflection-based fallback —
    // making the omission loud + fixable.
});
// ─── OpenAPI (R5.3 Phase B Task B.7 / D.1) ───────────────────────────────────
//
// Spec generation + Scalar UI is opt-in outside Development to avoid leaking
// surface in production. Set `Platform__OpenApi__Enabled=true` (env var) or
// `Platform:OpenApi:Enabled=true` in configuration to enable in Production.
//
// openapi-typed-client (Platform/ADR-0035): this gate — and the runtime
// AddOpenApi()/MapOpenApi()/MapScalarApiReference() surface it controls — is
// UNCHANGED by that change. ADR-0035 exports the document via CI-runtime
// capture (start the host with Platform:OpenApi:Enabled=true, curl
// /openapi/v1.json) rather than a build-time generator, specifically so this
// gate and the registration below never need to move. See the ADR for why the
// build-time alternative (Microsoft.Extensions.ApiDescription.Server) was
// tried and rejected: this host's ~28 IHostedServices (Pro Realtime/Cluster/
// EventStore eager Postgres schema migration + DI resolution during
// `app.Run()`'s startup phase, before any request is served) make a
// no-live-DB design-time export infeasible without invasive changes across
// multiple Pro packages, which is out of scope for an Api-host-only change.
var openApiEnabled = builder.Environment.IsDevelopment()
    || builder.Configuration.GetValue<bool>("Platform:OpenApi:Enabled");

if (openApiEnabled)
{
    builder.Services.AddOpenApi();
}

var app = builder.Build();

// ─── Phase 2.2 — ensure cluster_distributed_lock schema (leader election) ────
// AddPostgresDistributedLock registers the lock primitive but does NOT create its
// table; the SDK ships MigrationRunner.EnsureSchemaAsync as an explicit opt-in.
// Without this a leader's TryAcquireAsync fails on first tick with
// relation "cluster_distributed_lock" does not exist. Mirrors Realtime Program.cs.
// W3 — gated on clusterConn ALONE (no longer Asterisk:Ari): the keyed "Cluster"
// datasource + at least one lease (the always-on agent-liveness sweep) now exist for
// EVERY clustered deployment, voice or not, so the lock table must always be ensured.
if (!string.IsNullOrEmpty(clusterConn))
{
    using var clusterMigrationScope = app.Services.CreateScope();
    var clusterLockDataSource = clusterMigrationScope.ServiceProvider
        .GetRequiredKeyedService<NpgsqlDataSource>("Cluster");
    var clusterMigrationLogger = clusterMigrationScope.ServiceProvider
        .GetRequiredService<ILogger<Program>>();
    await Verbara.Sdk.Cluster.Postgres.Migrations.MigrationRunner.EnsureSchemaAsync(
        clusterLockDataSource,
        clusterMigrationLogger,
        app.Lifetime.ApplicationStopping);
}

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
// Middleware-order invariant: TenantResolutionMiddleware MUST run BEFORE UseRateLimiter()
// so the "per-tenant" partition resolver sees Items["TenantId"] (otherwise every request
// collapses to the shared "__global__" bucket). UseRateLimiter() MUST stay BEFORE
// UseAuthentication() so throttling happens before the (expensive) auth work.
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<RateLimitHeadersMiddleware>();
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
// K8s liveness vs readiness contract:
//   /health (liveness) → process-alive only. NO dependency checks. Returns
//     200 OK in <1 ms as long as the .NET host is responsive. Kubernetes
//     uses this to decide whether to RESTART the pod — restarting because a
//     dependency (Postgres / Asterisk / Redis) is degraded never helps; it
//     just amplifies the outage by cycling pods that would have recovered.
//   /health/ready (readiness) → process AND its dependencies. Returns 200
//     only if all "ready"-tagged checks pass. Kubernetes uses this to
//     decide whether to ROUTE TRAFFIC to the pod. Failures stop traffic but
//     leave the pod alive to recover.
//
// Before this split, both endpoints ran the FULL check suite (Postgres SQL
// ping, Asterisk AMI ping, background-service heartbeats, retention). Under
// burst load, the per-request /health latency exceeded the chart's default
// `livenessProbe.timeoutSeconds: 1` and Kubernetes restart-cascaded pods
// during otherwise-survivable traffic spikes (R5.5 Phase B-LK 2026-05-24:
// 4 pod restarts during VU=1500 presence + 250-500 RPS bursts, evidence
// `docs/operations/r55-blk-evidence/2026-05-24-v251-baseline/`).
//
// Predicate = _ => false → run NO checks; default formatter returns plain
// text "Healthy" with HTTP 200. <1 ms response, immune to dependency state.
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false,
});
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
// Voice call-control endpoints (3B.2c blind transfer) — only when AMI is configured, since the
// leader-gated IVoiceCallControlService is registered under the same voice-AMI gate.
if (!string.IsNullOrWhiteSpace(app.Configuration["Asterisk:Ami:Hostname"]))
    v1.MapVoiceEndpoints();
v1.MapAgentEndpoints();
v1.MapAdminEndpoints();
v1.MapQueueMembersEndpoints();
v1.MapFlowEndpoints();
v1.MapChannelConfigEndpoints();
v1.MapContactEndpoints();
v1.MapSetupEndpoints();
v1.MapManagementTenantEndpoints();
v1.MapManagementSystemEndpoints();
v1.MapManagementClusterEndpoints();
v1.MapManagementApiKeyEndpoints();
v1.MapAgentAssistFeatureEndpoints();
v1.MapSseEndpoints();

// ADR-0022 Phase A — PlatformHub moved to Verbara.Platform.Realtime. Service-to-
// service internal endpoints expose the data Realtime needs (agent→tenant cache
// + hub audit sink) over HTTP+JSON gated by X-Service-Key.
app.MapInternalIntegrationEndpoints(builder.Configuration["Services:ServiceKey"]);
v1.MapMediaEndpoints();
v1.MapCampaignEndpoints();
v1.MapCallAttemptEndpoints();
v1.MapDncListEndpoints();
v1.MapCallerIdPoolEndpoints();
v1.MapHolidayCalendarEndpoints();
v1.MapDialerSettingsEndpoints();
v1.MapTrunkEndpoints();
v1.MapVoiceMetadataEndpoints();
v1.MapDidRouteEndpoints();
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
// csat-runner Phase B — CSAT capture (webchat token-verified / email+sms internal
// service-key gated) + per-queue analytics read. License-gated (LicenseFeature.CsatRunner).
v1.MapCsatResponseEndpoints(builder.Configuration["Services:ServiceKey"]);
// CSAT admin template CRUD (csat-runner Phase E). AdminOnly + audit; manages the
// csat_templates store the ICsatTemplateProvider resolves prompts through. preview-voice
// returns HTTP 501 (Pro voice/TTS synthesis deferred).
v1.MapCsatTemplateAdminEndpoints();
v1.MapTypificationEndpoints();
// P2c.1 — per-tenant BYO LLM provider admin (typification:ai:configure gated, no license gate).
v1.MapTenantLlmConfigEndpoints();
// P2c.2 — tenant AI credit usage readout (GET /admin/ai/credits).
v1.MapAiCreditsEndpoints();
// c1 (credit-ledger-topups) — operator top-up (POST /management/credit-ledger/top-up) +
// tenant balance/entries read API (GET /admin/credit-ledger/{balance,entries}).
v1.MapCreditLedgerEndpoints();
v1.MapReasonHintEndpoints();
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
