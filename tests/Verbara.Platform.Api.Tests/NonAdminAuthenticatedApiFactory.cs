using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Like <see cref="AuthenticatedPlatformApiFactory"/> but seeds an Agent-role user so
/// endpoints gated with <c>AdminOnly</c> respond with 403. Used to exercise RBAC 403
/// paths for the new queue-members endpoints (R5.1 Task I).
/// </summary>
public sealed class NonAdminAuthenticatedApiFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "non-admin-test-key-88888";
    public const string TestTenantId = "tenant-nonadmin-001";
    private const string TestUserId = "non-admin-user";

    private static readonly string s_hashedKey = HashKey(TestApiKey);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            SetupAgentRoleAuth(services, s_hashedKey, TestTenantId, TestUserId);

            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);

            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
        });

        var host = base.CreateHost(builder);
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, TestTenantId);
        // ADR-0027 — Customer-typed tenant so RequireOperationalTenant lets
        // operational endpoints reach the AdminOnly authorization step (where
        // they then return 403 because the test user is non-Admin). Without
        // the seed, my filter would short-circuit at 401 (defense in depth)
        // before the authorization layer evaluates the policy.
        AuthenticatedPlatformApiFactory.SeedTestCustomerTenant(host.Services, TestTenantId);
        return host;
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TestTenantId);
        return client;
    }

    private static void SetupAgentRoleAuth(
        IServiceCollection services, string hashedKey, string tenantId, string userId)
    {
        var userEntityId = EntityId.From(userId);
        var tenantId_ = new TenantId(tenantId);

        var akDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IApiKeyStore));
        if (akDescriptor is not null) services.Remove(akDescriptor);

        var apiKeyStore = Substitute.For<IApiKeyStore>();
        var apiKey = new ApiKey
        {
            KeyId = EntityId.From("nonadmin-key-id"),
            TenantId = tenantId_,
            Name = "NonAdmin Key",
            HashedKey = hashedKey,
            Scopes = ["*"],
            UserId = userEntityId,
            IsRevoked = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        apiKeyStore.GetByHashAsync(hashedKey, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<ApiKey?>(apiKey));
        services.AddSingleton(apiKeyStore);

        // AHH Phase 1: filter !IsKeyedService (keyed-as-inner preserved).
        var userStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IUserStore) && !d.IsKeyedService);
        if (userStoreDescriptor is not null) services.Remove(userStoreDescriptor);

        var userStore = Substitute.For<IUserStore>();
        var testUser = new User
        {
            UserId = userEntityId,
            TenantId = tenantId_,
            Email = "non-admin@test.internal",
            DisplayName = "Non Admin Test User",
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        userStore.GetByIdAsync(tenantId_, userEntityId, Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<User?>(testUser));
        userStore.ListAsync(Arg.Any<TenantId>(), Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
                 .Returns(ci => Task.FromResult(PagedResult<User>.Empty(
                     ci.ArgAt<PagedQuery>(1).Page,
                     ci.ArgAt<PagedQuery>(1).PageSize)));
        services.AddSingleton(userStore);
    }

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}
