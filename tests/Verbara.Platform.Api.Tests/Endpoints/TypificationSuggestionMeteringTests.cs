using System.Net;
using System.Text.Json.Nodes;
using Verbara.Platform.Billing;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Ai;
using Verbara.Platform.Typification.Stores;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// C3 (P2c.2) — quota pre-check + credit metering wired into
/// <c>POST /conversations/{id}/typification-suggestion</c> (the classify path). Metering and
/// the AiAnalysis quota gate apply ONLY when the resolved tenant LLM config is
/// <see cref="AiSource.PlatformManaged"/>; BYO is never metered and never quota-gated here.
///
/// <para>
/// Mirrors the BYO suggestion harness (<c>TypificationAiSuggestionTests</c>): a
/// <c>FakeAiClassifier</c> returns a canned <see cref="AiClassification"/> so the endpoint's
/// gating runs deterministically; the credit meter + quota service are NSubstitute doubles
/// swapped in via <c>WithWebHostBuilder.ConfigureServices</c>; the platform-managed
/// <see cref="TenantLlmConfig"/> is seeded into the in-memory <see cref="ITenantLlmConfigStore"/>.
/// </para>
/// </summary>
public sealed class TypificationSuggestionMeteringTests : IDisposable
{
    private static readonly TenantId s_tenantId = new(AuthenticatedPlatformApiFactory.TestTenantId);

    private const string RootNodeId = "root-1";
    private const string LeafNodeId = "leaf-1";
    private const string RootCode = "SALES";
    private const string LeafCode = "CLOSED_WON";

    private readonly AuthenticatedPlatformApiFactory _factory;

    public TypificationSuggestionMeteringTests()
    {
        _factory = new AuthenticatedPlatformApiFactory();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task GetSuggestion_ShouldDegradeAndNotMeter_WhenPlatformManagedAndQuotaSoftBlocks()
    {
        var classification = Classification(0.9, new LlmUsage(30, 70, 100));
        var meter = Substitute.For<ITypificationCreditMeter>();
        var quota = Substitute.For<IQuotaEnforcementService>();
        // SoftBlock: CheckQuotaAsync denies; GetQuotaStatusAsync reports a SoftBlock action so the
        // handler degrades to the empty suggestion (NOT 402) and never classifies/meters.
        quota.CheckQuotaAsync(Arg.Any<TenantId>(), UsageType.AiAnalysis, Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(new QuotaCheckResult(Allowed: false, Reason: "exhausted", UsagePercent: 100));
        quota.GetQuotaStatusAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new TenantQuotaStatus(s_tenantId,
                new TenantQuota { TenantId = s_tenantId, QuotaAction = QuotaAction.SoftBlock }, []));

        using var factory = WithFakesAndMeter(new FakeAiClassifier(classification), meter, quota);
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(factory, AiConfig(enabled: true, suggestThreshold: 0.5));
        await SeedPlatformManagedLlmConfigAsync(factory);

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertEmptySuggestionAsync(response);
        await meter.DidNotReceive().RecordAsync(
            Arg.Any<TenantId>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSuggestion_ShouldReturn402AndNotMeter_WhenPlatformManagedAndQuotaHardBlocks()
    {
        var classification = Classification(0.9, new LlmUsage(30, 70, 100));
        var meter = Substitute.For<ITypificationCreditMeter>();
        var quota = Substitute.For<IQuotaEnforcementService>();
        quota.CheckQuotaAsync(Arg.Any<TenantId>(), UsageType.AiAnalysis, Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(new QuotaCheckResult(Allowed: false, Reason: "exhausted", UsagePercent: 100));
        quota.GetQuotaStatusAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new TenantQuotaStatus(s_tenantId,
                new TenantQuota { TenantId = s_tenantId, QuotaAction = QuotaAction.HardBlock }, []));

        using var factory = WithFakesAndMeter(new FakeAiClassifier(classification), meter, quota);
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(factory, AiConfig(enabled: true, suggestThreshold: 0.5));
        await SeedPlatformManagedLlmConfigAsync(factory);

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        await meter.DidNotReceive().RecordAsync(
            Arg.Any<TenantId>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSuggestion_ShouldClassifyAndMeter_WhenPlatformManagedAndUnderAllowance()
    {
        var classification = Classification(0.9, new LlmUsage(30, 70, 100), modelId: "gpt-x");
        var meter = Substitute.For<ITypificationCreditMeter>();
        var quota = Substitute.For<IQuotaEnforcementService>();
        quota.CheckQuotaAsync(Arg.Any<TenantId>(), UsageType.AiAnalysis, Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(new QuotaCheckResult(Allowed: true, Reason: null, UsagePercent: 10));

        using var factory = WithFakesAndMeter(new FakeAiClassifier(classification), meter, quota);
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
    public async Task GetSuggestion_ShouldNotMeter_WhenByo()
    {
        var classification = Classification(0.9, new LlmUsage(30, 70, 100));
        var meter = Substitute.For<ITypificationCreditMeter>();
        var quota = Substitute.For<IQuotaEnforcementService>();

        using var factory = WithFakesAndMeter(new FakeAiClassifier(classification), meter, quota);
        using var client = AuthenticatedClient(factory);

        // No platform-managed config seeded → the tenant's config is absent (BYO floor). The handler
        // must NOT quota-gate or meter: BYO providers are paid for by the tenant, never metered here.
        var convId = await SeedSchemaAndConversationAsync(factory, AiConfig(enabled: true, suggestThreshold: 0.5));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
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
            PromptVersion: "p2c2",
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

    private WebApplicationFactory<Program> WithFakesAndMeter(
        FakeAiClassifier fakeClassifier, ITypificationCreditMeter meter, IQuotaEnforcementService quota) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                Replace<ITypificationAiClassifier>(services, fakeClassifier);
                Replace(services, meter);
                Replace(services, quota);
            }));

    private static void Replace<T>(IServiceCollection services, T instance) where T : class
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(T)).ToList())
            services.Remove(d);
        services.AddSingleton(instance);
    }

    private static HttpClient AuthenticatedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", AuthenticatedPlatformApiFactory.TestTenantId);
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
                Name = "Metering Schema",
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
                Owner = ConversationOwner.ForAgent(EntityId.From(AuthenticatedPlatformApiFactory.TestUserId)),
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        return convId;
    }

    /// <summary>
    /// Test double for <see cref="ITypificationAiClassifier"/> returning a canned classification
    /// (mirrors <c>TypificationAiSuggestionTests.FakeAiClassifier</c>).
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
}
