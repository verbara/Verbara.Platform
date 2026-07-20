using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.Analytics;
using Verbara.Sdk.Pro.CallAnalytics.Store;
using Verbara.Sdk.Pro.Dialer.Campaign;
using Verbara.Sdk.Pro.Dialer.Compliance;
using Verbara.Sdk.Pro.Dialer.Contacts;
using Verbara.Sdk.Pro.Dialer.Dispositions;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.Dialer.Scheduling;
using Verbara.Sdk.Pro.EventStore;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk;
using Verbara.Sdk.Pro.AgentAssist.Storage.Postgres.Stores;
using Verbara.Sdk.Pro.Licensing;
using Npgsql;
using Verbara.Platform.Queues;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Factory that pre-seeds an authenticated API key so tests can call protected endpoints.
/// Also registers all in-memory store fallbacks so endpoints that depend on conditionally-
/// registered Postgres stores (campaign, analytics) resolve correctly without a database.
/// </summary>
public class AuthenticatedPlatformApiFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "test-api-key-12345";
    public const string TestTenantId = "tenant-test-001";
    // Public so tests can seed entities (e.g. an Agent) owned by the
    // authenticated caller — the test API key maps to this user id.
    public const string TestUserId = "test-admin-user";

    private static readonly string s_hashedKey = HashKey(TestApiKey);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // ── Auth (API key + admin user) ──────────────────────────────────
            SetupTestAuth(services, s_hashedKey, TestTenantId, TestUserId);

            // ── Asterisk SDK stubs (no real AMI/ARI connections in tests) ────
            StubVerbaraHostedServices(services);

            // ── Licensing ─────────────────────────────────────────────────────
            // v2.5.0-pro migration: ILicenseStatus substitute reports all features
            // licensed; no longer using removed LicenseOptions.EnforcementMode.
            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            // ── Dialer + Analytics stores ────────────────────────────────────
            RegisterInMemoryStores(services);

            // ── Realtime sync (overridable) ──────────────────────────────────
            // RegisterInMemoryStores already left a default no-op mock; this ADDS an
            // overridable instance on top (last registration wins) so a subclass can
            // inject a throwing stub for the best-effort-contract tests (ADR-0012
            // gate #6) without disturbing the other factories that share the static
            // helper. This closure runs after Startup, so it is the last word.
            services.AddSingleton(CreateRealtimeSyncService());
        });

        var host = base.CreateHost(builder);

        // Seed the feature gate cache with Enterprise features for the test tenant
        // so RequirePlanFeature filters pass (tests run with all features enabled).
        SeedEnterpriseFeatureGate(host.Services, TestTenantId);

        // ADR-0027 — seed the test tenant as Customer so RequireOperationalTenant
        // (applied to /admin/*, /queues/{id}/members, /conversations, etc.)
        // lets the existing Api.Tests suite pass through. Without this, the
        // gate would reject every operational call with HTTP 409 because the
        // TenantStatusMiddleware would return Items["Tenant"]=null when the
        // tenant lookup misses.
        SeedTestCustomerTenant(host.Services, TestTenantId);

        return host;
    }

    /// <summary>
    /// Inserts a single <see cref="TenantType.Customer"/> tenant matching
    /// <see cref="TestTenantId"/> into the wired in-memory <c>ITenantStore</c>
    /// so the <c>TenantStatusMiddleware</c> populates <c>Items["Tenant"]</c>
    /// for authenticated requests in tests.
    /// </summary>
    internal static void SeedTestCustomerTenant(IServiceProvider services, string tenantId)
    {
        using var scope = services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<Verbara.Sdk.Pro.MultiTenant.ITenantStore>();
        var tenant = new Verbara.Sdk.Pro.MultiTenant.Tenant
        {
            TenantId = tenantId,
            Name = "Test Customer",
            Status = Verbara.Sdk.Pro.MultiTenant.TenantStatus.Active,
            Type = Verbara.Sdk.Pro.MultiTenant.TenantType.Customer,
            ParentTenantId = null,
            Metadata = new Dictionary<string, string>
            {
                ["Plan"] = Verbara.Platform.Core.TenantPlan.Enterprise.ToString(),
                ["RateLimitTier"] = "Enterprise",
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        AwaitTenantUpsert(tenantStore, tenant);
    }

    private static void AwaitTenantUpsert(Verbara.Sdk.Pro.MultiTenant.ITenantStore tenantStore, Verbara.Sdk.Pro.MultiTenant.Tenant tenant)
    {
        // The store API exposes either Task or ValueTask depending on the impl;
        // hide the GetAwaiter().GetResult behind a tiny synchronous wrapper so
        // the analyzer doesn't flag CA2012 on a ValueTask-returning UpsertAsync.
        var task = tenantStore.UpsertAsync(tenant, CancellationToken.None);
        task.AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// The <see cref="Verbara.Sdk.Pro.Realtime.IRealtimeSyncService"/> registered for
    /// tests. Base returns a no-op NSubstitute mock; a subclass overrides it to inject
    /// a throwing stub (the best-effort-contract tests, ADR-0012 gate #6).
    /// </summary>
    protected virtual Verbara.Sdk.Pro.Realtime.IRealtimeSyncService CreateRealtimeSyncService()
        => Substitute.For<Verbara.Sdk.Pro.Realtime.IRealtimeSyncService>();

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TestTenantId);
        return client;
    }

    /// <summary>
    /// Replaces the real IApiKeyStore and IUserStore with substitutes that return a pre-seeded
    /// Admin test key and user so protected endpoints (AdminOnly, SupervisorPlus) can be called
    /// without a real database.
    /// </summary>
    internal static void SetupTestAuth(
        IServiceCollection services,
        string hashedKey,
        string tenantId,
        string userId)
    {
        var userEntityId = EntityId.From(userId);
        var tenantId_ = new TenantId(tenantId);

        // ── IApiKeyStore ─────────────────────────────────────────────────────
        var akDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IApiKeyStore));
        if (akDescriptor is not null) services.Remove(akDescriptor);

        var apiKeyStore = Substitute.For<IApiKeyStore>();
        var apiKey = new ApiKey
        {
            KeyId = EntityId.From("test-key-id"),
            TenantId = tenantId_,
            Name = "Test Key",
            HashedKey = hashedKey,
            Scopes = ["*"],
            UserId = userEntityId,
            IsRevoked = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        apiKeyStore.GetByHashAsync(hashedKey, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<ApiKey?>(apiKey));
        services.AddSingleton(apiKeyStore);

        // ── IUserStore ───────────────────────────────────────────────────────
        // Return an Admin user so AdminOnly and SupervisorPlus policies pass.
        // Filter !IsKeyedService — AHH Phase 1 keeps the concrete IUserStore
        // registered keyed-as-inner so the cache decorator can resolve it.
        var userStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IUserStore) && !d.IsKeyedService);
        if (userStoreDescriptor is not null) services.Remove(userStoreDescriptor);

        var userStore = Substitute.For<IUserStore>();
        var testUser = new User
        {
            UserId = userEntityId,
            TenantId = tenantId_,
            Email = "test-admin@test.internal",
            DisplayName = "Test Admin",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        userStore.GetByIdAsync(tenantId_, userEntityId, Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<User?>(testUser));
        // ListAsync must return a non-null PagedResult or the endpoint NREs on
        // `result.Items.Select(ToUserDto)`. Default NSubstitute returns null for
        // reference-typed Task<T> results, which was silently breaking ListUsers,
        // AuthTests.GetAdminUsers, and AuthIntegrationTests.ApiKey_ShouldReturn200_OnAdminUsers.
        // v1.14.3 — track created users in a per-substitute dictionary so:
        //  (a) the email-filter overload returns matching users, and
        //  (b) duplicate emails throw EntityAlreadyExistsException so the 409
        //      branch in AdminEndpoints.CreateUser is exercised.
        var seenUsers = new System.Collections.Generic.Dictionary<string, User>(StringComparer.OrdinalIgnoreCase);
        userStore.ListAsync(Arg.Any<TenantId>(), Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
                 .Returns(ci => Task.FromResult(PagedResult<User>.Empty(
                     ((PagedQuery)ci[1]).Page,
                     ((PagedQuery)ci[1]).PageSize)));
        userStore.ListAsync(Arg.Any<TenantId>(), Arg.Any<PagedQuery>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                 .Returns(ci =>
                 {
                     var query = (PagedQuery)ci[1];
                     var emailFilter = (string?)ci[2];
                     IEnumerable<User> pool = seenUsers.Values;
                     if (!string.IsNullOrWhiteSpace(emailFilter))
                         pool = pool.Where(u => u.Email is not null
                             && u.Email.Contains(emailFilter, StringComparison.OrdinalIgnoreCase));
                     var all = pool.ToList();
                     var page = all.Skip(query.Offset).Take(query.PageSize).ToList();
                     return Task.FromResult(new PagedResult<User>(page, all.Count, query.Page, query.PageSize));
                 });
        userStore.SaveAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
                 .Returns(ci =>
                 {
                     var u = (User)ci[0];
                     // Emulate idx_users_email UNIQUE: same email + different user_id ⇒ 409.
                     if (!string.IsNullOrEmpty(u.Email)
                         && seenUsers.TryGetValue(u.Email, out var existing)
                         && existing.UserId != u.UserId)
                     {
                         return Task.FromException(new EntityAlreadyExistsException("user", "email"));
                     }
                     seenUsers[u.Email ?? Guid.NewGuid().ToString()] = u;
                     return Task.CompletedTask;
                 });
        services.AddSingleton(userStore);
    }

    /// <summary>
    /// Replaces the real AMI connection and VerbaraServer with NSubstitute mocks and
    /// removes Asterisk-specific IHostedService registrations so the test host can start
    /// without connecting to a real Asterisk instance.
    /// </summary>
    internal static void StubVerbaraHostedServices(IServiceCollection services)
    {
        var hostedServices = services
            .Where(d => d.ServiceType == typeof(IHostedService) &&
                        (d.ImplementationType?.FullName?.Contains("Asterisk") == true ||
                         d.ImplementationType?.FullName?.Contains("Verbara") == true ||
                         d.ImplementationFactory is not null))
            .ToList();
        foreach (var d in hostedServices)
            services.Remove(d);

        // Replace IAmiConnection with a mock so any code that resolves it gets a no-op stub
        var amiDescriptors = services.Where(d => d.ServiceType == typeof(IAmiConnection)).ToList();
        foreach (var d in amiDescriptors) services.Remove(d);
        services.AddSingleton(Substitute.For<IAmiConnection>());

        // Replace IVerbaraServer with a mock
        var serverDescriptors = services.Where(d => d.ServiceType == typeof(IVerbaraServer)).ToList();
        foreach (var d in serverDescriptors) services.Remove(d);
        services.AddSingleton(Substitute.For<IVerbaraServer>());
    }

    internal static void RegisterInMemoryStores(IServiceCollection services)
    {
        // Campaign stores — always replace so Postgres stores (registered when connection string
        // is present in appsettings.json) do not attempt real DB connections in tests.
        RemoveAll<CampaignStoreBase>(services);
        RemoveAll<CampaignLifecycleManager>(services);
        services.AddSingleton<InMemoryCampaignStore>();
        services.AddSingleton<CampaignStoreBase>(sp => sp.GetRequiredService<InMemoryCampaignStore>());
        services.AddSingleton<CampaignLifecycleManager>(sp =>
            new CampaignLifecycleManager(
                sp.GetRequiredService<CampaignStoreBase>(),
                sp.GetRequiredService<ILogger<CampaignLifecycleManager>>()));

        RemoveAll<ContactListStoreBase>(services);
        services.AddSingleton<InMemoryContactListStore>();
        services.AddSingleton<ContactListStoreBase>(sp => sp.GetRequiredService<InMemoryContactListStore>());

        RemoveAll<DispositionCodeStoreBase>(services);
        services.AddSingleton<InMemoryDispositionCodeStore>();
        services.AddSingleton<DispositionCodeStoreBase>(sp => sp.GetRequiredService<InMemoryDispositionCodeStore>());

        // v0.5.0 Dialer config stores — always replace
        RemoveAll<TrunkStoreBase>(services);
        services.AddSingleton<TrunkStoreBase, InMemoryTrunkStore>();
        RemoveAll<OutboundRouteStoreBase>(services);
        services.AddSingleton<OutboundRouteStoreBase, InMemoryOutboundRouteStore>();
        RemoveAll<DncListStoreBase>(services);
        services.AddSingleton<DncListStoreBase, InMemoryDncListStore>();
        RemoveAll<CallerIdPoolStoreBase>(services);
        services.AddSingleton<CallerIdPoolStoreBase, InMemoryCallerIdPoolStore>();
        RemoveAll<HolidayCalendarStoreBase>(services);
        services.AddSingleton<HolidayCalendarStoreBase, InMemoryHolidayCalendarStore>();

        // Queue membership — always replace
        RemoveAll<IQueueMembershipStore>(services);
        services.AddSingleton<IQueueMembershipStore, InMemoryQueueMembershipStore>();

        // Analytics stores — always replace
        RemoveAll<ICompletedSessionStore>(services);
        services.AddSingleton<ICompletedSessionStore, InMemoryCompletedSessionStore>();
        RemoveAll<ICallAnalyticsStore>(services);
        services.AddSingleton<ICallAnalyticsStore, InMemoryCallAnalyticsStore>();
        RemoveAll<IIntervalSnapshotStore>(services);
        services.AddSingleton<IIntervalSnapshotStore, InMemoryIntervalSnapshotStore>();

        // Realtime — EndpointProfileStoreBase and IRealtimeSyncService are only wired up by
        // UsePostgresRealtimeStorage. Replace both with no-op substitutes so
        // DefaultEndpointProfileProvider and RealtimeSyncEngine can be activated without a DB.
        RemoveAll<EndpointProfileStoreBase>(services);
        services.AddSingleton<EndpointProfileStoreBase>(Substitute.For<EndpointProfileStoreBase>());

        // RealtimeSyncEngine (internal sealed) is registered as its own concrete type and as
        // IRealtimeSyncService. Remove both and replace IRealtimeSyncService with a mock so the
        // engine never attempts to resolve the internal IRealtimeStore.
        var syncEngineDescriptors = services
            .Where(d => d.ServiceType.FullName?.Contains("RealtimeSyncEngine") == true ||
                        d.ImplementationType?.FullName?.Contains("RealtimeSyncEngine") == true ||
                        (d.ImplementationFactory is not null &&
                         d.ServiceType == typeof(Verbara.Sdk.Pro.Realtime.IRealtimeSyncService)))
            .ToList();
        foreach (var d in syncEngineDescriptors) services.Remove(d);
        RemoveAll<Verbara.Sdk.Pro.Realtime.IRealtimeSyncService>(services);
        // Default no-op mock — this static helper is shared by several factories, so
        // it must leave a working IRealtimeSyncService registered. AuthenticatedPlatform-
        // ApiFactory.CreateHost ADDS an overridable instance on top (last-wins) so a
        // subclass can swap in a throwing stub without affecting other factories.
        services.AddSingleton(Substitute.For<Verbara.Sdk.Pro.Realtime.IRealtimeSyncService>());

        // AgentAssist Postgres stores (concrete sealed types used by endpoints).
        // Provide a dummy NpgsqlDataSource so stores can be constructed for DI resolution.
        // Actual DB calls will fail, but these endpoints are not exercised in unit tests.
        if (!services.Any(d => d.ServiceType == typeof(NpgsqlDataSource)))
            services.AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=test_unused"));
        if (!services.Any(d => d.ServiceType == typeof(AgentAssistSessionStore)))
            services.AddSingleton<AgentAssistSessionStore>();
        if (!services.Any(d => d.ServiceType == typeof(SuggestionLogStore)))
            services.AddSingleton<SuggestionLogStore>();
        if (!services.Any(d => d.ServiceType == typeof(ComplianceAlertStore)))
            services.AddSingleton<ComplianceAlertStore>();
    }

    /// <summary>
    /// Seeds the FeatureGateCache with Enterprise features for the given tenant so that
    /// RequirePlanFeature endpoint filters pass in tests (all features are enabled).
    /// </summary>
    internal static void SeedEnterpriseFeatureGate(IServiceProvider services, string tenantId)
    {
        var cache = services.GetRequiredService<Verbara.Platform.Api.Services.FeatureGateCache>();
        var plan = TenantPlan.Enterprise;
        cache.Set(tenantId, new Verbara.Platform.Api.Services.ResolvedFeatures(
            plan,
            PlanDefinition.GetFeatures(plan),
            PlanDefinition.GetMaxChannels(plan),
            PlanDefinition.GetAuditRetentionDays(plan),
            PlanDefinition.GetMaxWebhookSubscriptions(plan),
            PlanDefinition.GetMaxScheduledReports(plan)));
    }

    private static void RemoveAll<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}
