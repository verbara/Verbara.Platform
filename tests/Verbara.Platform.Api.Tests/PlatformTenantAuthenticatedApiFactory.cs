using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// ADR-0027 — integration-test factory that mirrors
/// <see cref="AuthenticatedPlatformApiFactory"/> but seeds the test tenant
/// as <see cref="TenantType.Platform"/> instead of Customer. Used by
/// <c>TenantTypeGateTests</c> to assert that operational endpoints reject
/// callers from the platform host tenant with HTTP 409.
/// </summary>
public sealed class PlatformTenantAuthenticatedApiFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "platform-typed-test-api-key";
    public const string TestTenantId = "platform-typed-test-tenant";
    public const string TestUserId = "platform-typed-test-user";

    private static readonly string s_hashedKey = HashKey(TestApiKey);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            AuthenticatedPlatformApiFactory.SetupTestAuth(services, s_hashedKey, TestTenantId, TestUserId);
            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);
            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
        });

        var host = base.CreateHost(builder);

        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, TestTenantId);

        // Critical difference vs the base factory: seed the test tenant as
        // Platform-typed so RequireOperationalTenant rejects operational
        // endpoints with HTTP 409 + the TenantTypeMismatchProblem body.
        SeedPlatformTenant(host.Services, TestTenantId);

        return host;
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TestTenantId);
        return client;
    }

    private static void SeedPlatformTenant(IServiceProvider services, string tenantId)
    {
        using var scope = services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var tenant = new Tenant
        {
            TenantId = tenantId,
            Name = "Test Platform Host",
            Status = TenantStatus.Active,
            Type = TenantType.Platform,
            ParentTenantId = null,
            Metadata = new Dictionary<string, string>
            {
                ["Plan"] = Verbara.Platform.Core.TenantPlan.Enterprise.ToString(),
                ["RateLimitTier"] = "Enterprise",
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        tenantStore.UpsertAsync(tenant, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}
