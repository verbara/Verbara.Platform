using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// c2 (credit-ledger-lots) — factory for the operator Promo/Partner grant surface and the partner derive-on-read
/// attribution. Seeds a Platform → Partner → Customer hierarchy (plus a Customer directly under Platform, for the
/// "parent is not a Partner" negative path) and TWO management keys: a full-scope key (passes the
/// <c>billing:credits:grant</c> double-lock) and a narrow-scope key (lacks the grant permission → rejected).
/// </summary>
public sealed class CreditLedgerGrantsApiFactory : WebApplicationFactory<Program>
{
    public const string HostTenantId = "clg-platform";
    public const string PartnerTenantId = "clg-partner";
    public const string CustomerUnderPartnerId = "clg-customer-of-partner";
    public const string CustomerUnderPlatformId = "clg-customer-of-platform";

    // Full-scope management key — satisfies billing:credits:grant via the legacy platform:* blanket.
    public const string GrantMgmtKey = "clg-mgmt-grant-key";
    // Narrow-scope management key — has only audit.read, so the billing:credits:grant gate rejects it.
    public const string NoGrantMgmtKey = "clg-mgmt-nogrant-key";

    private static readonly string s_hashedGrantKey = HashKey(GrantMgmtKey);
    private static readonly string s_hashedNoGrantKey = HashKey(NoGrantMgmtKey);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

            // Replace IApiKeyStore with a substitute that resolves BOTH management keys by hash.
            foreach (var d in services.Where(d => d.ServiceType == typeof(IApiKeyStore)).ToList())
                services.Remove(d);

            var apiKeyStore = Substitute.For<IApiKeyStore>();
            apiKeyStore.GetByHashAsync(s_hashedGrantKey, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<ApiKey?>(new ApiKey
                {
                    KeyId = EntityId.From("clg-grant-key-id"),
                    TenantId = new TenantId(HostTenantId),
                    Name = "CLG Grant Key",
                    HashedKey = s_hashedGrantKey,
                    Scopes = ["platform:*"],
                    KeyType = ApiKeyType.Management,
                    CreatedAt = DateTimeOffset.UtcNow,
                }));
            apiKeyStore.GetByHashAsync(s_hashedNoGrantKey, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<ApiKey?>(new ApiKey
                {
                    KeyId = EntityId.From("clg-nogrant-key-id"),
                    TenantId = new TenantId(HostTenantId),
                    Name = "CLG No-Grant Key",
                    HashedKey = s_hashedNoGrantKey,
                    Scopes = ["audit.read"], // explicitly NOT billing:credits:grant and NOT platform:*
                    KeyType = ApiKeyType.Management,
                    CreatedAt = DateTimeOffset.UtcNow,
                }));
            services.AddSingleton(apiKeyStore);

            // IUserStore is unused on the management-key path (key has no UserId) but must resolve for DI.
            if (!services.Any(d => d.ServiceType == typeof(IUserStore)))
                services.AddSingleton(Substitute.For<IUserStore>());

            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);
        });

        var host = base.CreateHost(builder);
        SeedTenants(host.Services);
        return host;
    }

    private static void SeedTenants(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<ITenantStore>();

        Seed(tenantStore.UpsertAsync(new Tenant
        {
            TenantId = HostTenantId,
            Name = "CLG Platform",
            Status = TenantStatus.Active,
            Type = TenantType.Platform,
            ParentTenantId = null,
        }));

        Seed(tenantStore.UpsertAsync(new Tenant
        {
            TenantId = PartnerTenantId,
            Name = "CLG Partner",
            Status = TenantStatus.Active,
            Type = TenantType.Partner,
            ParentTenantId = HostTenantId,
        }));

        Seed(tenantStore.UpsertAsync(new Tenant
        {
            TenantId = CustomerUnderPartnerId,
            Name = "CLG Customer (under Partner)",
            Status = TenantStatus.Active,
            Type = TenantType.Customer,
            ParentTenantId = PartnerTenantId,
        }));

        Seed(tenantStore.UpsertAsync(new Tenant
        {
            TenantId = CustomerUnderPlatformId,
            Name = "CLG Customer (under Platform)",
            Status = TenantStatus.Active,
            Type = TenantType.Customer,
            ParentTenantId = HostTenantId,
        }));
    }

    /// <summary>Client authenticated with the full-scope management key (passes the grant gate).</summary>
    public HttpClient CreateGrantClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {GrantMgmtKey}");
        return client;
    }

    /// <summary>Client authenticated with the narrow-scope management key (lacks billing:credits:grant).</summary>
    public HttpClient CreateNoGrantClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {NoGrantMgmtKey}");
        return client;
    }

    private static void Seed(ValueTask vt) => vt.AsTask().GetAwaiter().GetResult();

    private static string HashKey(string rawKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));
}
