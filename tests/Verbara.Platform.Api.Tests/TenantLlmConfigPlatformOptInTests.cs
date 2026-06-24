using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Llm;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// C1 (P2c.2) — platform-managed opt-in on <c>/admin/ai/llm-config</c>: a PUT that sets
/// <c>aiSource=PlatformManaged</c> is gated by <see cref="PlanFeature.PlatformLlm"/> (403 without the
/// entitlement, 200 + persisted <see cref="AiSource.PlatformManaged"/> with it), and GET echoes
/// <c>aiSource</c> + <c>platformLlmAvailable</c> from the feature-gate cache. BYO is unaffected.
/// </summary>
public sealed class TenantLlmConfigPlatformOptInTests
{
    private const string TenantId = PlatformOptInApiFactory.TestTenantId;

    [Fact]
    public async Task Put_ShouldReject_WhenPlatformManagedWithoutEntitlement()
    {
        using var factory = new PlatformOptInApiFactory(platformLlmEntitled: false);
        using var client = factory.CreateAuthenticatedClient();

        var body = new
        {
            providerType = "OpenAiCompatible",
            model = "gpt-4o-mini",
            apiKey = (string?)null,
            settings = new { baseUrl = "https://api.openai.com/v1" },
            enabled = true,
            aiSource = "PlatformManaged",
        };

        var response = await client.PutAsync("/api/admin/ai/llm-config", JsonContent.Create(body));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // Nothing must persist when the entitlement is missing.
        (await LoadConfigAsync(factory)).Should().BeNull();
    }

    [Fact]
    public async Task Put_ShouldAccept_WhenPlatformManagedWithEntitlement()
    {
        using var factory = new PlatformOptInApiFactory(platformLlmEntitled: true);
        using var client = factory.CreateAuthenticatedClient();

        var body = new
        {
            providerType = "OpenAiCompatible",
            model = "gpt-4o-mini",
            apiKey = (string?)null,
            settings = new { baseUrl = "https://api.openai.com/v1" },
            enabled = true,
            aiSource = "PlatformManaged",
        };

        var response = await client.PutAsync("/api/admin/ai/llm-config", JsonContent.Create(body));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["aiSource"]!.GetValue<string>().Should().Be("PlatformManaged");

        var stored = await LoadConfigAsync(factory);
        stored.Should().NotBeNull();
        stored!.AiSource.Should().Be(AiSource.PlatformManaged);
        // PlatformManaged does NOT require a BYO key.
        stored.ApiKey.Should().BeNull();
    }

    [Fact]
    public async Task Put_ShouldRemainByo_WhenAiSourceOmitted()
    {
        using var factory = new PlatformOptInApiFactory(platformLlmEntitled: true);
        using var client = factory.CreateAuthenticatedClient();

        // An existing BYO caller that omits aiSource must default to Byo (source-compatible).
        var body = new
        {
            providerType = "OpenAiCompatible",
            model = "gpt-4o-mini",
            apiKey = "sk-byo-1234",
            settings = new { baseUrl = "https://api.openai.com/v1" },
            enabled = true,
        };

        var response = await client.PutAsync("/api/admin/ai/llm-config", JsonContent.Create(body));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await LoadConfigAsync(factory);
        stored.Should().NotBeNull();
        stored!.AiSource.Should().Be(AiSource.Byo);
        stored.ApiKey.Should().Be("sk-byo-1234");
    }

    [Fact]
    public async Task Get_ShouldEchoAiSourceAndAvailability_WhenEntitled()
    {
        using var factory = new PlatformOptInApiFactory(platformLlmEntitled: true);
        using var client = factory.CreateAuthenticatedClient();

        await SeedConfigAsync(factory, AiSource.PlatformManaged);

        var response = await client.GetAsync("/api/admin/ai/llm-config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["aiSource"]!.GetValue<string>().Should().Be("PlatformManaged");
        json["platformLlmAvailable"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Get_ShouldReportAvailabilityFalse_WhenNotEntitled()
    {
        using var factory = new PlatformOptInApiFactory(platformLlmEntitled: false);
        using var client = factory.CreateAuthenticatedClient();

        await SeedConfigAsync(factory, AiSource.Byo);

        var response = await client.GetAsync("/api/admin/ai/llm-config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["aiSource"]!.GetValue<string>().Should().Be("Byo");
        json["platformLlmAvailable"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Get_ShouldReportAvailability_WhenNoRow()
    {
        using var factory = new PlatformOptInApiFactory(platformLlmEntitled: true);
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/admin/ai/llm-config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["configured"]!.GetValue<bool>().Should().BeFalse();
        json["platformLlmAvailable"]!.GetValue<bool>().Should().BeTrue();
    }

    // ─── helpers ────────────────────────────────────────────────────────────────────

    private static async Task SeedConfigAsync(PlatformOptInApiFactory factory, AiSource source)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantLlmConfigStore>();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(new TenantLlmConfig
        {
            TenantId = EntityId.From(TenantId),
            ProviderType = ProviderType.OpenAiCompatible,
            Model = "gpt-4o-mini",
            ApiKey = source == AiSource.Byo ? "sk-seed-0001" : null,
            ApiKeyLast4 = source == AiSource.Byo ? "0001" : null,
            Settings = new ProviderSettings { BaseUrl = "https://api.openai.com/v1" },
            Enabled = true,
            AiSource = source,
            CreatedAt = now,
            UpdatedAt = now,
        }, CancellationToken.None);
    }

    private static async Task<TenantLlmConfig?> LoadConfigAsync(PlatformOptInApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantLlmConfigStore>();
        return await store.GetAsync(EntityId.From(TenantId), CancellationToken.None);
    }

    /// <summary>
    /// Mirrors the BYO <c>LlmConfigApiFactory</c> harness (grants <c>typification:ai:configure</c>,
    /// seeds the customer tenant), but seeds the FeatureGateCache with a plan whose feature set either
    /// includes or omits <see cref="PlanFeature.PlatformLlm"/> per <c>platformLlmEntitled</c>.
    /// </summary>
    private sealed class PlatformOptInApiFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "test-api-key-12345";
        public const string TestTenantId = "tenant-test-001";
        public const string TestUserId = "test-admin-user";

        private static readonly string s_hashedKey = HashKey(TestApiKey);

        private readonly bool _platformLlmEntitled;

        public PlatformOptInApiFactory(bool platformLlmEntitled)
        {
            _platformLlmEntitled = platformLlmEntitled;
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

                // Grant typification:ai:configure for the owning user id (the API-key user_id claim).
                var roleStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IUserRoleStore));
                if (roleStoreDescriptor is not null) services.Remove(roleStoreDescriptor);
                var roleStore = Substitute.For<IUserRoleStore>();
                var perms = new HashSet<string>(StringComparer.Ordinal) { "typification:ai:configure" };
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
            // The TenantStatusMiddleware re-derives the FeatureGateCache from the tenant's Plan
            // metadata on every request, so the entitlement is driven by the SEEDED PLAN — Enterprise
            // (all features incl. PlatformLlm) when entitled, Starter (no features) when not.
            SeedCustomerTenant(host.Services, _platformLlmEntitled ? TenantPlan.Enterprise : TenantPlan.Starter);
            return host;
        }

        public HttpClient CreateAuthenticatedClient()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TestApiKey}");
            client.DefaultRequestHeaders.Add("X-Tenant-Id", TestTenantId);
            return client;
        }

        /// <summary>
        /// Seeds the test customer tenant with the given plan so the <c>TenantStatusMiddleware</c>
        /// resolves a feature set that includes (<see cref="TenantPlan.Enterprise"/>) or omits
        /// (<see cref="TenantPlan.Starter"/>) <see cref="PlanFeature.PlatformLlm"/>. Mirrors
        /// <c>AuthenticatedPlatformApiFactory.SeedTestCustomerTenant</c> but with a parameterized plan.
        /// </summary>
        private static void SeedCustomerTenant(IServiceProvider services, TenantPlan plan)
        {
            using var scope = services.CreateScope();
            var tenantStore = scope.ServiceProvider.GetRequiredService<Verbara.Sdk.Pro.MultiTenant.ITenantStore>();
            var tenant = new Verbara.Sdk.Pro.MultiTenant.Tenant
            {
                TenantId = TestTenantId,
                Name = "Test Customer",
                Status = Verbara.Sdk.Pro.MultiTenant.TenantStatus.Active,
                Type = Verbara.Sdk.Pro.MultiTenant.TenantType.Customer,
                ParentTenantId = null,
                Metadata = new Dictionary<string, string>
                {
                    ["Plan"] = plan.ToString(),
                },
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            tenantStore.UpsertAsync(tenant, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        private static string HashKey(string rawKey)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey));
            return Convert.ToHexStringLower(bytes);
        }
    }
}
