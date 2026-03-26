using System.Security.Cryptography;
using System.Text;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.Analytics;
using Asterisk.Sdk.Pro.CallAnalytics.Store;
using Asterisk.Sdk.Pro.Dialer.Campaign;
using Asterisk.Sdk.Pro.Dialer.Compliance;
using Asterisk.Sdk.Pro.Dialer.Contacts;
using Asterisk.Sdk.Pro.Dialer.Dispositions;
using Asterisk.Sdk.Pro.Dialer.Routing;
using Asterisk.Sdk.Pro.Dialer.Scheduling;
using Asterisk.Sdk.Pro.EventStore;
using Asterisk.Sdk;
using Asterisk.Sdk.Pro.AgentAssist.Storage.Postgres.Stores;
using Asterisk.Sdk.Pro.Licensing;
using Npgsql;
using Asterisk.Platform.Queues;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests;

/// <summary>
/// Factory that pre-seeds an authenticated API key so tests can call protected endpoints.
/// Also registers all in-memory store fallbacks so endpoints that depend on conditionally-
/// registered Postgres stores (campaign, analytics) resolve correctly without a database.
/// </summary>
public sealed class AuthenticatedPlatformApiFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "test-api-key-12345";
    public const string TestTenantId = "tenant-test-001";
    private const string TestUserId = "test-admin-user";

    private static readonly string s_hashedKey = HashKey(TestApiKey);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // ── Auth (API key + admin user) ──────────────────────────────────
            SetupTestAuth(services, s_hashedKey, TestTenantId, TestUserId);

            // ── Asterisk SDK stubs (no real AMI/ARI connections in tests) ────
            StubAsteriskHostedServices(services);

            // ── Licensing ─────────────────────────────────────────────────────
            services.Configure<LicenseOptions>(o => o.EnforcementMode = EnforcementMode.Disabled);
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            // ── Dialer + Analytics stores ────────────────────────────────────
            RegisterInMemoryStores(services);
        });

        return base.CreateHost(builder);
    }

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
        var userStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IUserStore));
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
        services.AddSingleton(userStore);
    }

    /// <summary>
    /// Replaces the real AMI connection and AsteriskServer with NSubstitute mocks and
    /// removes Asterisk-specific IHostedService registrations so the test host can start
    /// without connecting to a real Asterisk instance.
    /// </summary>
    internal static void StubAsteriskHostedServices(IServiceCollection services)
    {
        // Remove Asterisk-specific hosted services that try to connect to a real server.
        // We identify them by their implementation type to avoid removing framework-level services.
        var hostedServices = services
            .Where(d => d.ServiceType == typeof(IHostedService) &&
                        (d.ImplementationType?.FullName?.Contains("Asterisk") == true ||
                         d.ImplementationFactory is not null))
            .ToList();
        foreach (var d in hostedServices)
            services.Remove(d);

        // Replace IAmiConnection with a mock so any code that resolves it gets a no-op stub
        var amiDescriptors = services.Where(d => d.ServiceType == typeof(IAmiConnection)).ToList();
        foreach (var d in amiDescriptors) services.Remove(d);
        services.AddSingleton(Substitute.For<IAmiConnection>());

        // Replace IAsteriskServer with a mock
        var serverDescriptors = services.Where(d => d.ServiceType == typeof(IAsteriskServer)).ToList();
        foreach (var d in serverDescriptors) services.Remove(d);
        services.AddSingleton(Substitute.For<IAsteriskServer>());
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
