using System.Net;
using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Regression tests for <see cref="Endpoints.ManagementClusterEndpoints"/>.
///
/// The /management/* endpoints are the platform administration surface. They are
/// gated by the PlatformAdminOnly policy, and MUST NOT carry a LicenseFeature gate:
/// blocking a platform admin from managing their own cluster when no license is
/// loaded is an operational hole, not a safeguard. The license gate protects
/// tenant-facing features (dialer, analytics, agent-assist), not the admin console.
///
/// This fixture forces <see cref="LicenseFeature.None"/> — the exact state of
/// a fresh demo where no license file has been seeded — and asserts that every
/// /management/cluster/* route is reachable by a platform admin. The Pro v2.5.0-pro
/// LicenseGateMiddleware always 402s endpoints carrying <c>LicenseFeatureMetadata</c>
/// when the requested feature isn't licensed; /management/* routes are intentionally
/// NOT decorated with <c>LicenseFeatureMetadata</c>, so the middleware short-circuits
/// at the first metadata check (see <see cref="Middleware.LicenseGateMiddleware"/>).
/// </summary>
public sealed class ManagementClusterLicenseGateTests
    : IClassFixture<ManagementClusterLicenseGateTests.EnforcedNoLicenseFactory>
{
    private readonly HttpClient _client;

    public ManagementClusterLicenseGateTests(EnforcedNoLicenseFactory factory)
    {
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task ClusterStatus_ShouldReturn200_WhenNoLicenseAndEnforceMode()
    {
        var response = await _client.GetAsync("/api/management/cluster/status");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "/management/* is platform administration; license gate must not block it");
    }

    [Fact]
    public async Task ClusterNodes_ShouldReturn200_WhenNoLicenseAndEnforceMode()
    {
        var response = await _client.GetAsync("/api/management/cluster/nodes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ClusterInstances_ShouldReturn200_WhenNoLicenseAndEnforceMode()
    {
        var response = await _client.GetAsync("/api/management/cluster/instances");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ClusterDrainStatus_ShouldNotReturnLicenseBlockStatuses_WhenNoLicenseAndEnforceMode()
    {
        var response = await _client.GetAsync("/api/management/cluster/drain-status");

        // Endpoint may return 200 or 404 depending on cluster state, but never the
        // license-block status codes: 403 (legacy v2.1.0) or 402 (Pro v2.4.0-pro
        // contract change). Management routes don't carry LicenseFeatureMetadata, so
        // the LicenseGate middleware passes them through.
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        response.StatusCode.Should().NotBe(HttpStatusCode.PaymentRequired);
    }

    /// <summary>
    /// Variant of <see cref="PlatformAdminApiFactory"/> that flips licensing to the
    /// worst-case "no license loaded + enforce mode" demo scenario.
    /// </summary>
    public sealed class EnforcedNoLicenseFactory : WebApplicationFactory<Program>
    {
        public const string HostTenantId = "platform";
        public const string TestMgmtApiKey = "mgmt-enforced-test-key";

        private static readonly string s_hashedMgmtKey = HashKey(TestMgmtApiKey);

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

                // Real-world demo: NO licensed features (Pro v2.5.0-pro: no
                // EnforcementMode knob — license absence alone drives the gate).
                services.AddNoProFeaturesLicensed();

                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);
            });

            var host = base.CreateHost(builder);

            using var scope = host.Services.CreateScope();
            var tenantStore = scope.ServiceProvider.GetRequiredService<ITenantStore>();
            var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();
            var apiKeyStore = scope.ServiceProvider.GetRequiredService<IApiKeyStore>();

            var tenantId = new TenantId(HostTenantId);

            tenantStore.UpsertAsync(new Tenant
            {
                TenantId = HostTenantId,
                Name = "Test Platform (No License)",
                Status = TenantStatus.Active,
                Type = TenantType.Platform,
                ParentTenantId = null,
            }).AsTask().GetAwaiter().GetResult();

            userStore.SaveAsync(new User
            {
                UserId = EntityId.From("platform-admin-user-enforced"),
                TenantId = tenantId,
                Email = "platform-admin-enforced@test.internal",
                DisplayName = "Test Platform Admin (Enforced)",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None).GetAwaiter().GetResult();

            apiKeyStore.SaveAsync(new ApiKey
            {
                KeyId = EntityId.From("mgmt-enforced-key-id"),
                TenantId = tenantId,
                Name = "Test Management Key (Enforced)",
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

        private static string HashKey(string rawKey)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
            return Convert.ToHexStringLower(bytes);
        }
    }
}
