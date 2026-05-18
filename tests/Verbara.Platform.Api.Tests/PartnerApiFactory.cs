// Back-compat tests: EnforcementMode is [Obsolete] in Pro v2.4.0-pro but kept functional until v2.5.0-pro.
#pragma warning disable CS0618

using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Factory with a pre-seeded hierarchy: Platform tenant → Partner tenant → Customer tenant.
///
/// Auth is wired via <see cref="AuthenticatedPlatformApiFactory.SetupTestAuth"/> (same pattern
/// as all other working factories) so the mock <see cref="Verbara.Platform.Identity.IApiKeyStore"/>
/// returns the correct <c>tenant_id = PartnerTenantId</c> claim.  The real
/// <see cref="ITenantStore"/> (InMemory singleton) is seeded after host creation so that
/// <see cref="Verbara.Platform.Api.Auth.PartnerAdminAuthorizationHandler"/> can verify the
/// tenant is of type <see cref="TenantType.Partner"/>.
/// </summary>
public sealed class PartnerApiFactory : WebApplicationFactory<Program>
{
    public const string HostTenantId = "partner-test-platform";
    public const string PartnerTenantId = "partner-test-partner";
    public const string CustomerTenantId = "partner-test-customer";
    public const string TestPartnerApiKey = "partner-test-api-key-99999";
    public const string TestPartnerUserId = "partner-admin-user";

    private static readonly string s_hashedPartnerKey = HashKey(TestPartnerApiKey);

    // Expose billing stores so tests can seed data directly.
    public IPartnerRevenueStore RevenueStore { get; private set; } = null!;
    public IRateCardStore RateCardStore { get; private set; } = null!;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // ── Auth: mock IApiKeyStore + IUserStore, same pattern as all working factories.
            // SetupTestAuth returns tenant_id=PartnerTenantId + user_id=TestPartnerUserId
            // + ClaimTypes.Role="Admin" so PartnerAdminOnly and permission policies both pass.
            AuthenticatedPlatformApiFactory.SetupTestAuth(
                services, s_hashedPartnerKey, PartnerTenantId, TestPartnerUserId);

            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

            services.Configure<Verbara.Sdk.Pro.Licensing.LicenseOptions>(
                o => o.EnforcementMode = Verbara.Sdk.Pro.Licensing.EnforcementMode.Disabled);
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);
        });

        var host = base.CreateHost(builder);

        SeedTenants(host.Services);

        // Expose billing stores for test seeding
        RevenueStore = host.Services.GetRequiredService<IPartnerRevenueStore>();
        RateCardStore = host.Services.GetRequiredService<IRateCardStore>();

        return host;
    }

    /// <summary>
    /// Seeds the real <see cref="ITenantStore"/> singleton with the three-level hierarchy.
    /// The mock IApiKeyStore/IUserStore are already wired by SetupTestAuth above; we only
    /// need the tenant records so PartnerAdminAuthorizationHandler can verify TenantType.Partner.
    /// </summary>
    private static void SeedTenants(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var tenantStore = sp.GetRequiredService<ITenantStore>();

        // 1. Platform (host) tenant
        Seed(tenantStore.UpsertAsync(new Tenant
        {
            TenantId = HostTenantId,
            Name = "Test Platform",
            Status = TenantStatus.Active,
            Type = TenantType.Platform,
            ParentTenantId = null,
            Metadata = new Dictionary<string, string>
            {
                ["Plan"] = TenantPlan.Enterprise.ToString(),
                ["RateLimitTier"] = "Enterprise",
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        }));

        // 2. Partner tenant (child of platform)
        Seed(tenantStore.UpsertAsync(new Tenant
        {
            TenantId = PartnerTenantId,
            Name = "Test Partner",
            Status = TenantStatus.Active,
            Type = TenantType.Partner,
            ParentTenantId = HostTenantId,
            Metadata = new Dictionary<string, string>
            {
                ["Plan"] = TenantPlan.Enterprise.ToString(),
                ["RateLimitTier"] = "Enterprise",
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        }));

        // 3. Customer tenant (child of partner)
        Seed(tenantStore.UpsertAsync(new Tenant
        {
            TenantId = CustomerTenantId,
            Name = "Test Customer",
            Status = TenantStatus.Active,
            Type = TenantType.Customer,
            ParentTenantId = PartnerTenantId,
            Options = new TenantOptions
            {
                MaxConcurrentChannels = 100,
                MaxActiveCampaigns = 10,
            },
            Metadata = new Dictionary<string, string>
            {
                ["Plan"] = TenantPlan.Pro.ToString(),
                ["RateLimitTier"] = "Professional",
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        }));

        // Seed Enterprise feature gates for partner and customer tenants
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(services, PartnerTenantId);
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(services, CustomerTenantId);
    }

    /// <summary>Returns a client authenticated as the Partner tenant admin.</summary>
    public HttpClient CreatePartnerClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TestPartnerApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", PartnerTenantId);
        return client;
    }

    /// <summary>Returns an unauthenticated client for testing 401 responses.</summary>
    public HttpClient CreateAnonymousClient() => CreateClient();

    // Convenience wrapper: synchronously awaits a ValueTask (test-only, no concurrency risk).
    private static void Seed(ValueTask vt) => vt.AsTask().GetAwaiter().GetResult();

    private static string HashKey(string rawKey)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}
