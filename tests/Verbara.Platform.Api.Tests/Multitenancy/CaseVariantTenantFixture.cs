using System.Net.Http.Headers;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests.Multitenancy;

/// <summary>
/// A test host whose tenant store holds tenant ids that differ only in case, side by side — the shape the
/// database allows (<c>tenants.tenant_id</c> is a case-sensitive <c>TEXT</c> primary key) and that tenants created
/// before v2.41.1 may still have. Seeds:
/// <list type="bullet">
///   <item><description>The Platform host tenant <c>platform</c>, and a Customer <c>PLATFORM</c> beside it.</description></item>
///   <item><description>Customers <c>ACME</c> and <c>acme</c>, both children of the host.</description></item>
///   <item><description>A Partner <c>partner-k</c> with its Customer <c>partner-kcust</c>, and a Customer
///   <c>PARTNER-K</c> beside the Partner.</description></item>
/// </list>
/// One Admin per tenant; JWTs are minted by the real <see cref="JwtTokenService"/> so the principal carries the
/// stored tenant id as its <c>tid</c>.
/// </summary>
public sealed class CaseVariantTenantFixture : WebApplicationFactory<Program>
{
    public const string HostTenantId = "platform";
    public const string HostVariantTenantId = "PLATFORM";
    public const string UpperTenantId = "ACME";
    public const string LowerTenantId = "acme";
    public const string PartnerTenantId = "partner-k";
    public const string PartnerCustomerTenantId = "partner-kcust";
    public const string PartnerVariantTenantId = "PARTNER-K";

    private static readonly string[] AllTenants =
    [
        HostTenantId, HostVariantTenantId, UpperTenantId, LowerTenantId,
        PartnerTenantId, PartnerCustomerTenantId, PartnerVariantTenantId,
    ];

    private bool _seeded;
    private readonly Lock _seedLock = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);
        });

        var host = base.CreateHost(builder);
        Seed(host.Services);
        foreach (var tenantId in AllTenants)
            AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, tenantId);
        return host;
    }

    private void Seed(IServiceProvider services)
    {
        lock (_seedLock)
        {
            if (_seeded) return;
            _seeded = true;
        }

        using var scope = services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();

        Upsert(tenantStore, HostTenantId, TenantType.Platform, parent: null);
        Upsert(tenantStore, HostVariantTenantId, TenantType.Customer, parent: HostTenantId);
        Upsert(tenantStore, UpperTenantId, TenantType.Customer, parent: HostTenantId);
        Upsert(tenantStore, LowerTenantId, TenantType.Customer, parent: HostTenantId);
        Upsert(tenantStore, PartnerTenantId, TenantType.Partner, parent: HostTenantId);
        Upsert(tenantStore, PartnerCustomerTenantId, TenantType.Customer, parent: PartnerTenantId);
        Upsert(tenantStore, PartnerVariantTenantId, TenantType.Customer, parent: HostTenantId);

        foreach (var tenantId in AllTenants)
        {
            userStore.CreateAsync(new User
            {
                UserId = EntityId.From(AdminUserId(tenantId)),
                TenantId = new TenantId(tenantId),
                Email = AdminEmail(tenantId),
                DisplayName = AdminEmail(tenantId),
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private static void Upsert(ITenantStore store, string tenantId, TenantType type, string? parent)
    {
        store.UpsertAsync(new Tenant
        {
            TenantId = tenantId,
            Name = tenantId,
            Status = TenantStatus.Active,
            Type = type,
            ParentTenantId = parent,
        }).AsTask().GetAwaiter().GetResult();
    }

    // The user id and email must not collide between the two cases of one tenant id: the in-memory user store
    // keys users by id and compares emails ignoring case.
    private static string AdminUserId(string tenantId) => $"admin-{Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(tenantId))}";

    private static string AdminEmail(string tenantId) => $"{AdminUserId(tenantId)}@test.internal";

    /// <summary>An HTTP client carrying a freshly minted Admin JWT whose <c>tid</c> is <paramref name="tenantId"/>.</summary>
    public HttpClient CreateAdminClient(string tenantId)
    {
        string token;
        using (var scope = Services.CreateScope())
        {
            var jwt = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
            var user = new User
            {
                UserId = EntityId.From(AdminUserId(tenantId)),
                TenantId = new TenantId(tenantId),
                Email = AdminEmail(tenantId),
                DisplayName = AdminEmail(tenantId),
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            (token, _) = jwt.GenerateAccessTokenAsync(user).GetAwaiter().GetResult();
        }

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
