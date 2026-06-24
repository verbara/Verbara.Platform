using System.Net;
using System.Text.Json.Nodes;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// C2 (P2c.2) — <c>GET /admin/ai/credits</c>: reads the tenant quota + the current-period
/// <see cref="UsageType.AiAnalysis"/> token summary and converts tokens to AI Credits via
/// <c>PlatformLlmOptions.CreditTokenRatio</c> (default 1000). Seeds <c>AiCreditsMonthly=10</c> and
/// 5000 consumed tokens → expects 5 consumed / 5 remaining / 50% with the period end + action.
/// </summary>
public sealed class AiCreditsEndpointTests
{
    private const string TenantId = AiCreditsApiFactory.TestTenantId;

    [Fact]
    public async Task Get_ShouldReturnCreditUsage_WhenQuotaAndUsageSeeded()
    {
        using var factory = new AiCreditsApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await SeedQuotaAsync(factory, aiCredits: 10, QuotaAction.SoftBlock);
        await SeedUsageAsync(factory, tokens: 5000m);

        var response = await client.GetAsync("/api/admin/ai/credits");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["allowanceCredits"]!.GetValue<long>().Should().Be(10);
        json["consumedCredits"]!.GetValue<long>().Should().Be(5);
        json["remainingCredits"]!.GetValue<long>().Should().Be(5);
        json["usagePercent"]!.GetValue<double>().Should().Be(50);
        json["actionOnExhaustion"]!.GetValue<string>().Should().Be("SoftBlock");
    }

    [Fact]
    public async Task Get_ShouldReportUnlimited_WhenNoQuotaConfigured()
    {
        using var factory = new AiCreditsApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await SeedUsageAsync(factory, tokens: 3000m);

        var response = await client.GetAsync("/api/admin/ai/credits");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["allowanceCredits"].Should().BeNull();
        json["consumedCredits"]!.GetValue<long>().Should().Be(3);
        // Null allowance = unlimited: remaining is null and percent is 0.
        json["remainingCredits"].Should().BeNull();
        json["usagePercent"]!.GetValue<double>().Should().Be(0);
    }

    [Fact]
    public async Task Get_ShouldReportZeroConsumed_WhenNoUsageRecorded()
    {
        using var factory = new AiCreditsApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await SeedQuotaAsync(factory, aiCredits: 10, QuotaAction.HardBlock);

        var response = await client.GetAsync("/api/admin/ai/credits");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["consumedCredits"]!.GetValue<long>().Should().Be(0);
        json["remainingCredits"]!.GetValue<long>().Should().Be(10);
        json["usagePercent"]!.GetValue<double>().Should().Be(0);
        json["actionOnExhaustion"]!.GetValue<string>().Should().Be("HardBlock");
    }

    [Fact]
    public async Task Get_ShouldForbid_WhenCallerLacksConfigurePermission()
    {
        using var factory = new AiCreditsApiFactory(grantConfigurePermission: false);
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/admin/ai/credits");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ─── helpers ────────────────────────────────────────────────────────────────────

    private static async Task SeedQuotaAsync(AiCreditsApiFactory factory, long aiCredits, QuotaAction action)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantQuotaStore>();
        await store.UpsertAsync(new TenantQuota
        {
            TenantId = new TenantId(TenantId),
            AiCreditsMonthly = aiCredits,
            QuotaAction = action,
        }, CancellationToken.None);
    }

    private static async Task SeedUsageAsync(AiCreditsApiFactory factory, decimal tokens)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IUsageRecordStore>();
        await store.SaveAsync(new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = new TenantId(TenantId),
            UsageType = UsageType.AiAnalysis,
            Quantity = tokens,
            Unit = UsageUnit.Tokens,
            ReferenceId = "conv-seed",
            RecordedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
    }

    /// <summary>
    /// Mirrors the BYO/opt-in llm-config harness: seeds the customer tenant + grants (or withholds)
    /// <c>typification:ai:configure</c> on the API-key owning user. Billing InMemory stores come from
    /// the host's <c>AddInMemoryStorage()</c>; <c>PlatformLlmOptions</c> resolves with its default
    /// 1000 token/credit ratio (never wired to a per-tenant key).
    /// </summary>
    private sealed class AiCreditsApiFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "test-api-key-12345";
        public const string TestTenantId = "tenant-test-001";
        public const string TestUserId = "test-admin-user";

        private static readonly string s_hashedKey = HashKey(TestApiKey);

        private readonly bool _grantConfigurePermission;

        public AiCreditsApiFactory(bool grantConfigurePermission = true)
        {
            _grantConfigurePermission = grantConfigurePermission;
        }

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

                var roleStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IUserRoleStore));
                if (roleStoreDescriptor is not null) services.Remove(roleStoreDescriptor);
                var roleStore = Substitute.For<IUserRoleStore>();
                var perms = _grantConfigurePermission
                    ? new HashSet<string>(StringComparer.Ordinal) { "typification:ai:configure" }
                    : new HashSet<string>(StringComparer.Ordinal);
                var empty = new HashSet<string>(StringComparer.Ordinal);
                roleStore.GetEffectivePermissionsAsync(
                        Arg.Any<TenantId>(),
                        Arg.Is<EntityId>(e => e.Value == TestUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlySet<string>>(perms));
                roleStore.GetEffectivePermissionsAsync(
                        Arg.Any<TenantId>(),
                        Arg.Is<EntityId>(e => e.Value != TestUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlySet<string>>(empty));
                services.AddSingleton(roleStore);
            });

            var host = base.CreateHost(builder);
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

        private static string HashKey(string rawKey)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey));
            return Convert.ToHexStringLower(bytes);
        }
    }
}
