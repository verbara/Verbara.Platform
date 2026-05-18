// Back-compat tests: EnforcementMode is [Obsolete] in Pro v2.4.0-pro but kept functional until v2.5.0-pro.
#pragma warning disable CS0618

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Verbara.Platform.Api.Tests.Endpoints.Security;

/// <summary>
/// Regression coverage for the <c>PREPUB-2026-05-09-ADMIN-002</c> back-compat
/// path on <c>JwtKeyEndpoints.RotateKey</c> (the canary "high-value gate").
/// Two scenarios:
/// <list type="bullet">
///   <item>A management key carrying narrow scopes (without
///   <c>security.jwt.rotate</c>) must be REJECTED — proves the
///   short-circuit is gone.</item>
/// </list>
/// The legacy <c>platform:*</c> back-compat path is already covered by the
/// existing <see cref="JwtKeyEndpointsTests.RotateKey_ShouldReturn200AndEmitAudit_WhenAuthorized"/>.
/// </summary>
public sealed class JwtKeyEndpointsScopeTests
{
    [Fact]
    public async Task RotateKey_ShouldReturn403_WhenManagementKeyMissingJwtRotateScope()
    {
        await using var factory = new NarrowScopeManagementKeyFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Authorization", $"Bearer {NarrowScopeManagementKeyFactory.NarrowScopeKey}");

        var response = await client.PostAsync(
            "/api/v1/management/security/jwt/rotate-key",
            content: null);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "ADMIN-002 fix: a management key whose scopes do not include 'security.jwt.rotate' must NOT reach the JWT rotation endpoint");
    }

    /// <summary>
    /// Mirrors <see cref="PlatformAdminApiFactory"/> but seeds a management API
    /// key whose <c>Scopes</c> are a narrow whitelist (<c>audit.read</c>) — i.e.
    /// what a future least-privilege issuance default produces.
    /// </summary>
    private sealed class NarrowScopeManagementKeyFactory : WebApplicationFactory<Program>
    {
        public const string HostTenantId = "platform";
        public const string NarrowScopeKey = "mgmt-test-narrow-key";

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
            using var scope = host.Services.CreateScope();
            var tenantStore = scope.ServiceProvider.GetRequiredService<ITenantStore>();
            var apiKeyStore = scope.ServiceProvider.GetRequiredService<IApiKeyStore>();

            tenantStore.UpsertAsync(new Tenant
            {
                TenantId = HostTenantId,
                Name = "Narrow Scope Host",
                Status = TenantStatus.Active,
                Type = TenantType.Platform,
                ParentTenantId = null,
            }).AsTask().GetAwaiter().GetResult();

            var hashed = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(NarrowScopeKey)));

            apiKeyStore.SaveAsync(new ApiKey
            {
                KeyId = EntityId.From("mgmt-narrow-key-id"),
                TenantId = new TenantId(HostTenantId),
                Name = "Narrow Scope Key",
                HashedKey = hashed,
                // Critical: NOT platform:* and NOT security.jwt.rotate.
                Scopes = ["audit.read"],
                KeyType = ApiKeyType.Management,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None).GetAwaiter().GetResult();

            return host;
        }
    }
}
