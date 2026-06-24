using System.Net;
using System.Text.Json.Nodes;
using Verbara.Platform.Billing;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Llm;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Ai;
using Verbara.Platform.Typification.Stores;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// ADR-0032 — runtime entitlement re-check on the typification classify path
/// (<c>POST /conversations/{id}/typification-suggestion</c>). A tenant whose stored
/// <see cref="TenantLlmConfig.AiSource"/> is <see cref="AiSource.PlatformManaged"/> but who no
/// longer has <see cref="PlanFeature.PlatformLlm"/> at runtime (plan downgrade / add-on expiry)
/// MUST degrade to the empty suggestion (HTTP 200), BEFORE the quota pre-check and the classifier,
/// so it is never quota-gated (no 402) and never metered (no involuntary billing). BYO tenants are
/// unaffected (the block is guarded by <c>isPlatformManaged</c>).
///
/// <para>
/// Mirrors <c>TypificationSuggestionMeteringTests</c>'s harness (FakeAiClassifier + NSubstitute
/// credit-meter/quota doubles + seeded platform-managed <see cref="TenantLlmConfig"/>) but drives
/// the entitlement axis through the SEEDED TENANT PLAN — exactly like
/// <c>TenantLlmConfigPlatformOptInTests.PlatformOptInApiFactory</c>: Enterprise grants
/// <see cref="PlanFeature.PlatformLlm"/>, Starter omits it, and <c>TenantStatusMiddleware</c>
/// re-derives the <c>FeatureGateCache</c> from the plan on every request. The license axis
/// (AdvancedTypification + TypificationAi) stays fully licensed so the route is still reachable.
/// </para>
/// </summary>
public sealed class TypificationSuggestionEntitlementTests
{
    private static readonly TenantId s_tenantId = new(EntitlementApiFactory.TestTenantId);

    private const string RootNodeId = "root-1";
    private const string LeafNodeId = "leaf-1";
    private const string RootCode = "SALES";
    private const string LeafCode = "CLOSED_WON";

    [Fact]
    public async Task GetSuggestion_ShouldDegradeToEmpty_WhenPlatformManagedAndEntitlementMissing()
    {
        var classification = Classification(0.9, new LlmUsage(30, 70, 100), modelId: "gpt-x");
        var meter = Substitute.For<ITypificationCreditMeter>();
        var quota = Substitute.For<IQuotaEnforcementService>();

        // Starter plan ⇒ no PlatformLlm entitlement, even though the stored config says PlatformManaged.
        using var factory = new EntitlementApiFactory(platformLlmEntitled: false)
            .WithFakesAndMeter(new FakeAiClassifier(classification), meter, quota);
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(factory, AiConfig(enabled: true, suggestThreshold: 0.5));
        await SeedPlatformManagedLlmConfigAsync(factory);

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertEmptySuggestionAsync(response);
    }

    [Fact]
    public async Task GetSuggestion_ShouldNotMeterOrCheckQuota_WhenEntitlementMissing()
    {
        var classification = Classification(0.9, new LlmUsage(30, 70, 100), modelId: "gpt-x");
        var meter = Substitute.For<ITypificationCreditMeter>();
        var quota = Substitute.For<IQuotaEnforcementService>();

        using var factory = new EntitlementApiFactory(platformLlmEntitled: false)
            .WithFakesAndMeter(new FakeAiClassifier(classification), meter, quota);
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(factory, AiConfig(enabled: true, suggestThreshold: 0.5));
        await SeedPlatformManagedLlmConfigAsync(factory);

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await meter.DidNotReceive().RecordAsync(
            Arg.Any<TenantId>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await quota.DidNotReceive().CheckQuotaAsync(
            Arg.Any<TenantId>(), Arg.Any<UsageType>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSuggestion_ShouldClassifyAndMeter_WhenPlatformManagedAndEntitled()
    {
        var classification = Classification(0.9, new LlmUsage(30, 70, 100), modelId: "gpt-x");
        var meter = Substitute.For<ITypificationCreditMeter>();
        var quota = Substitute.For<IQuotaEnforcementService>();
        quota.CheckQuotaAsync(Arg.Any<TenantId>(), UsageType.AiAnalysis, Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(new QuotaCheckResult(Allowed: true, Reason: null, UsagePercent: 10));

        // Enterprise plan ⇒ PlatformLlm entitled: the new gate must NOT block an entitled tenant.
        using var factory = new EntitlementApiFactory(platformLlmEntitled: true)
            .WithFakesAndMeter(new FakeAiClassifier(classification), meter, quota);
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(factory, AiConfig(enabled: true, suggestThreshold: 0.5));
        await SeedPlatformManagedLlmConfigAsync(factory);

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["suggestedNodePath"].Should().NotBeNull();

        await meter.Received(1).RecordAsync(
            Arg.Is<TenantId>(t => t.Value == s_tenantId.Value),
            convId.Value,
            30, 70, 100,
            "gpt-x",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSuggestion_ShouldNotApplyEntitlementCheck_WhenByo()
    {
        var classification = Classification(0.9, new LlmUsage(30, 70, 100), modelId: "gpt-x");
        var meter = Substitute.For<ITypificationCreditMeter>();
        var quota = Substitute.For<IQuotaEnforcementService>();

        // BYO floor: no platform-managed config seeded AND the plan lacks PlatformLlm. The handler must
        // NOT short-circuit on the missing entitlement (gate is guarded by isPlatformManaged): BYO
        // classifies and is never quota-gated nor metered.
        using var factory = new EntitlementApiFactory(platformLlmEntitled: false)
            .WithFakesAndMeter(new FakeAiClassifier(classification), meter, quota);
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(factory, AiConfig(enabled: true, suggestThreshold: 0.5));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["suggestedNodePath"].Should().NotBeNull();

        await meter.DidNotReceive().RecordAsync(
            Arg.Any<TenantId>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await quota.DidNotReceive().CheckQuotaAsync(
            Arg.Any<TenantId>(), Arg.Any<UsageType>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static AiClassification Classification(double confidence, LlmUsage usage, string modelId = "test-model") =>
        new(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string>(),
            Confidence: confidence,
            Sentiment: "positive",
            ModelId: modelId,
            PromptVersion: "adr0032",
            Usage: usage);

    private static async Task AssertEmptySuggestionAsync(HttpResponseMessage response)
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["suggestedNodePath"].Should().BeNull();
        json["suggestedFieldValues"].Should().BeNull();
        json["confidence"].Should().BeNull();
        json["sentiment"].Should().BeNull();
        json["band"]!.GetValue<string>().Should().Be("None");
    }

    private static TypificationAiConfig AiConfig(bool enabled, double suggestThreshold) =>
        new()
        {
            Enabled = enabled,
            Mode = AiMode.SuggestOnly,
            SuggestThreshold = suggestThreshold,
            SentimentGating = false,
            EntityFieldMap = new Dictionary<string, string>(),
        };

    private static HttpClient AuthenticatedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {EntitlementApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", EntitlementApiFactory.TestTenantId);
        return client;
    }

    private static async Task SeedPlatformManagedLlmConfigAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantLlmConfigStore>();
        await store.UpsertAsync(new TenantLlmConfig
        {
            TenantId = EntityId.From(s_tenantId.Value),
            ProviderType = ProviderType.OpenAiCompatible,
            Model = "ignored-when-platform",
            AiSource = AiSource.PlatformManaged,
            Enabled = true,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        }, CancellationToken.None);
    }

    private static async Task<EntityId> SeedSchemaAndConversationAsync(
        WebApplicationFactory<Program> factory, TypificationAiConfig aiConfig)
    {
        var schemaId = EntityId.New();
        var convId = EntityId.New();

        using var scope = factory.Services.CreateScope();
        var schemaStore = scope.ServiceProvider.GetRequiredService<ITypificationSchemaStore>();
        var bindingStore = scope.ServiceProvider.GetRequiredService<ISchemaBindingStore>();
        var conversationStore = scope.ServiceProvider.GetRequiredService<IConversationStore>();

        await schemaStore.SaveAsync(
            new TypificationSchema
            {
                SchemaId = schemaId,
                TenantId = s_tenantId,
                Name = "Entitlement Schema",
                Version = 1,
                IsPublished = true,
                MaxDepth = 5,
                Nodes =
                [
                    new TypificationNode
                    {
                        NodeId = EntityId.From(RootNodeId),
                        ParentNodeId = null,
                        Label = "Sales",
                        Code = RootCode,
                        SortOrder = 0,
                        IsLeaf = false,
                    },
                    new TypificationNode
                    {
                        NodeId = EntityId.From(LeafNodeId),
                        ParentNodeId = EntityId.From(RootNodeId),
                        Label = "Closed Won",
                        Code = LeafCode,
                        SortOrder = 0,
                        IsLeaf = true,
                        Leaf = new LeafOutcome { Category = TypificationCategory.Success, IsActive = true },
                    },
                ],
                Fields = [],
                DataDips = [],
                AiConfig = aiConfig,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        await bindingStore.SaveAsync(
            new SchemaBinding
            {
                BindingId = EntityId.New(),
                TenantId = s_tenantId,
                Scope = BindingScope.Tenant,
                ScopeRef = null,
                SchemaId = schemaId,
                SubTreeRootNodeId = null,
                Priority = 10,
                AiConfigOverride = null,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        await conversationStore.SaveAsync(
            new Conversation
            {
                ConversationId = convId,
                TenantId = s_tenantId,
                ContactId = EntityId.New(),
                Channel = ChannelType.WebChat,
                State = ConversationState.Active,
                Owner = ConversationOwner.ForAgent(EntityId.From(EntitlementApiFactory.TestUserId)),
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        return convId;
    }

    /// <summary>
    /// Test double for <see cref="ITypificationAiClassifier"/> returning a canned classification
    /// (mirrors <c>TypificationSuggestionMeteringTests.FakeAiClassifier</c>).
    /// </summary>
    private sealed class FakeAiClassifier : ITypificationAiClassifier
    {
        private readonly AiClassification? _result;

        public FakeAiClassifier(AiClassification? result) => _result = result;

        public Task<AiClassification?> ClassifyAsync(
            EntityId tenantId,
            TypificationSchema schema,
            EntityId? subtreeRoot,
            Conversation conversation,
            IReadOnlyList<Message> transcript,
            CancellationToken ct) => Task.FromResult(_result);
    }

    /// <summary>
    /// Mirrors <c>TenantLlmConfigPlatformOptInTests.PlatformOptInApiFactory</c> (license-grants
    /// AdvancedTypification + TypificationAi via <c>AddAllProFeaturesLicensed</c>; seeds the customer
    /// tenant Active) but parameterizes the tenant PLAN — Enterprise (has PlatformLlm) when entitled,
    /// Starter (does not) when not — so <c>TenantStatusMiddleware</c> re-derives the FeatureGateCache
    /// per request and drives the ADR-0032 runtime entitlement re-check. Exposes
    /// <see cref="WithFakesAndMeter"/> to swap in the metering-test doubles.
    /// </summary>
    private sealed class EntitlementApiFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "test-api-key-12345";
        public const string TestTenantId = "tenant-test-001";
        public const string TestUserId = "test-admin-user";

        private static readonly string s_hashedKey = HashKey(TestApiKey);

        private readonly bool _platformLlmEntitled;
        private Action<IServiceCollection>? _extraServices;

        public EntitlementApiFactory(bool platformLlmEntitled) => _platformLlmEntitled = platformLlmEntitled;

        /// <summary>
        /// Swaps the classifier for a canned <see cref="FakeAiClassifier"/> and replaces the credit
        /// meter + quota service with NSubstitute doubles (mirrors
        /// <c>TypificationSuggestionMeteringTests.WithFakesAndMeter</c>). Returns a derived factory.
        /// </summary>
        public EntitlementApiFactory WithFakesAndMeter(
            FakeAiClassifier fakeClassifier, ITypificationCreditMeter meter, IQuotaEnforcementService quota)
        {
            _extraServices = services =>
            {
                Replace<ITypificationAiClassifier>(services, fakeClassifier);
                Replace(services, meter);
                Replace(services, quota);
            };
            return this;
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
                _extraServices?.Invoke(services);
            });

            var host = base.CreateHost(builder);
            // TenantStatusMiddleware re-derives the FeatureGateCache from the tenant's Plan metadata on
            // every request — Enterprise grants PlatformLlm, Starter omits it.
            SeedCustomerTenant(host.Services, _platformLlmEntitled ? TenantPlan.Enterprise : TenantPlan.Starter);
            return host;
        }

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

        private static void Replace<T>(IServiceCollection services, T instance) where T : class
        {
            foreach (var d in services.Where(d => d.ServiceType == typeof(T)).ToList())
                services.Remove(d);
            services.AddSingleton(instance);
        }

        private static string HashKey(string rawKey)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey));
            return Convert.ToHexStringLower(bytes);
        }
    }
}
