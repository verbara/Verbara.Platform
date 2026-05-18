// Back-compat tests: EnforcementMode is [Obsolete] in Pro v2.4.0-pro but kept functional until v2.5.0-pro.
#pragma warning disable CS0618

using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Factory with a pre-seeded Platform (host) tenant, platform admin user,
/// and Management API key for testing /api/management/* and /api/setup endpoints.
/// </summary>
public sealed class PlatformAdminApiFactory : WebApplicationFactory<Program>
{
    public const string HostTenantId = "platform";
    public const string TestMgmtApiKey = "mgmt-test-key-platform";
    public const string TestPlatformAdminUserId = "platform-admin-user";

    private static readonly string s_hashedMgmtKey = HashKey(TestMgmtApiKey);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

            services.Configure<Verbara.Sdk.Pro.Licensing.LicenseOptions>(
                o => o.EnforcementMode = Verbara.Sdk.Pro.Licensing.EnforcementMode.Disabled);
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);
        });

        var host = base.CreateHost(builder);

        // Seed platform tenant, admin, and management key
        using var scope = host.Services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var apiKeyStore = scope.ServiceProvider.GetRequiredService<IApiKeyStore>();

        var tenantId = new TenantId(HostTenantId);

        // Host tenant
        tenantStore.UpsertAsync(new Tenant
        {
            TenantId = HostTenantId,
            Name = "Test Platform",
            Status = TenantStatus.Active,
            Type = TenantType.Platform,
            ParentTenantId = null,
        }).AsTask().GetAwaiter().GetResult();

        // Platform admin user
        userStore.SaveAsync(new User
        {
            UserId = EntityId.From(TestPlatformAdminUserId),
            TenantId = tenantId,
            Email = "platform-admin@test.internal",
            DisplayName = "Test Platform Admin",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None).GetAwaiter().GetResult();

        // Management API key
        apiKeyStore.SaveAsync(new ApiKey
        {
            KeyId = EntityId.From("mgmt-key-id"),
            TenantId = tenantId,
            Name = "Test Management Key",
            HashedKey = s_hashedMgmtKey,
            Scopes = ["platform:*"],
            KeyType = ApiKeyType.Management,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None).GetAwaiter().GetResult();

        return host;
    }

    public HttpClient CreatePlatformAdminClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TestMgmtApiKey}");
        return client;
    }

    /// <summary>Creates a client with no auth headers — for testing /api/setup.</summary>
    public HttpClient CreateAnonymousClient() => CreateClient();

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}
