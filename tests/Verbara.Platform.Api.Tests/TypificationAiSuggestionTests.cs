using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Ai;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// D1 (P2a) — <c>POST /conversations/{id}/typification-suggestion</c> (AI auto-disposition
/// suggestion) and D2 (P2a) — AutoAi provenance on <c>POST /conversations/{id}/typify</c>.
///
/// <para>
/// <b>Fake classifier:</b> the real <see cref="ITypificationAiClassifier"/> calls an
/// <c>ILlmProvider</c> (default-disabled in tests). To exercise the endpoint's gating logic
/// deterministically WITHOUT a live LLM, each test factory overrides
/// <see cref="ITypificationAiClassifier"/> with <see cref="FakeAiClassifier"/> (a canned
/// <see cref="AiClassification"/>) via <c>WithWebHostBuilder.ConfigureServices</c> — the
/// same late-registration-wins pattern <c>AddAllProFeaturesLicensed()</c> uses. This isolates
/// the endpoint's confidence/sentiment gates from the classifier's own JSON parsing.
/// </para>
///
/// <para>
/// <b>Isolation:</b> the typification + conversation stores are process-wide singletons
/// registered by <c>AddInMemoryStorage()</c>; each test owns a fresh factory so no
/// cross-test binding accumulation can occur (see <c>TypifyEndpointTests</c> remarks).
/// </para>
/// </summary>
public sealed class TypificationAiSuggestionTests : IDisposable
{
    private static readonly TenantId s_tenantId = new(AuthenticatedPlatformApiFactory.TestTenantId);

    private const string RootNodeId = "root-1";
    private const string LeafNodeId = "leaf-1";
    private const string RootCode = "SALES";
    private const string LeafCode = "CLOSED_WON";

    private readonly AuthenticatedPlatformApiFactory _factory;

    public TypificationAiSuggestionTests()
    {
        _factory = new AuthenticatedPlatformApiFactory();
    }

    public void Dispose() => _factory.Dispose();

    // ─── D1 — suggestion endpoint ────────────────────────────────────────────────

    [Fact]
    public async Task GetTypificationSuggestion_ShouldReturnSuggestion_WhenAiEnabledAndClassifierReturnsPath()
    {
        var classification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string> { ["outcome_notes"] = "deal closed" },
            Confidence: 0.9,
            Sentiment: "positive");

        using var factory = WithFakeClassifier(new FakeAiClassifier(classification));
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfig(enabled: true, suggestThreshold: 0.5));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["suggestedNodePath"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal(RootNodeId, LeafNodeId);
        json["confidence"]!.GetValue<double>().Should().Be(0.9);
        json["sentiment"]!.GetValue<string>().Should().Be("positive");
        json["suggestedFieldValues"]!["outcome_notes"]!.GetValue<string>().Should().Be("deal closed");
    }

    [Fact]
    public async Task GetTypificationSuggestion_ShouldReturn402_WhenTypificationAiUnlicensed()
    {
        // AdvancedTypification licensed, TypificationAi NOT → the combined-flags gate
        // (AdvancedTypification | TypificationAi) must still 402 (BOTH features required).
        // A dedicated factory licenses EXACTLY AdvancedTypification (the base
        // AuthenticatedPlatformApiFactory's AddAllProFeaturesLicensed() registration wins
        // over a WithWebHostBuilder override, so a subclass is the deterministic path).
        using var factory = new AdvancedTypificationOnlyLicenseFactory();
        using var client = factory.CreateAuthenticatedClient();

        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfig(enabled: true, suggestThreshold: 0.5));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
    }

    [Fact]
    public async Task GetTypificationSuggestion_ShouldReturnEmpty_WhenAiDisabled()
    {
        // Classifier would return a high-confidence path, but AiConfig.Enabled=false short-circuits.
        var classification = new AiClassification(
            [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            new Dictionary<string, string>(), 0.99, "positive");

        using var factory = WithFakeClassifier(new FakeAiClassifier(classification));
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfig(enabled: false, suggestThreshold: 0.0));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertEmptySuggestionAsync(response);
    }

    [Fact]
    public async Task GetTypificationSuggestion_ShouldReturnEmpty_WhenBelowSuggestThreshold()
    {
        var classification = new AiClassification(
            [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            new Dictionary<string, string>(), 0.4, "positive");

        using var factory = WithFakeClassifier(new FakeAiClassifier(classification));
        using var client = AuthenticatedClient(factory);

        // Threshold 0.8 > classifier 0.4 → suppressed.
        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfig(enabled: true, suggestThreshold: 0.8));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertEmptySuggestionAsync(response);
    }

    [Fact]
    public async Task GetTypificationSuggestion_ShouldReturnEmpty_WhenSentimentGatedSuccessLeafOnVeryNegative()
    {
        // Very-negative sentiment + a Success-category leaf + SentimentGating=true → suppressed.
        var classification = new AiClassification(
            [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            new Dictionary<string, string>(), 0.95, "very_negative");

        using var factory = WithFakeClassifier(new FakeAiClassifier(classification));
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfig(enabled: true, suggestThreshold: 0.5, sentimentGating: true));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertEmptySuggestionAsync(response);
    }

    // ─── B2 — shadow mode persists without surfacing ────────────────────────────

    [Fact]
    public async Task GetTypificationSuggestion_ShouldPersistSuggestion_WhenModeIsShadow()
    {
        var classification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string>(),
            Confidence: 0.9,
            Sentiment: "positive",
            ModelId: "test-model",
            PromptVersion: "p2b-2");

        using var factory = WithFakeClassifier(new FakeAiClassifier(classification));
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfigWithMode(enabled: true, mode: AiMode.Shadow, suggestThreshold: 0.5));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        // Shadow mode must return HTTP 200 but surface nothing to the agent.
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        await AssertEmptySuggestionAsync(response);

        // The suggestion must have been persisted in the store.
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAiSuggestionStore>();
        var saved = await store.GetLatestForConversationAsync(EntityId.From(s_tenantId.Value), convId, CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.SuggestedLeafNodeId.Value.Should().Be(LeafNodeId);
        saved.Confidence.Should().Be(0.9);
        saved.ModelId.Should().Be("test-model");
        saved.PromptVersion.Should().Be("p2b-2");
        saved.CommittedLeafNodeId.Should().BeNull("reconciliation happens in B3");
        saved.Accepted.Should().BeNull("reconciliation happens in B3");
    }

    [Fact]
    public async Task GetTypificationSuggestion_ShouldPersistAndReturn_WhenModeIsSuggestOnly()
    {
        var classification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string> { ["outcome_notes"] = "deal closed" },
            Confidence: 0.85,
            Sentiment: "neutral",
            ModelId: "test-model",
            PromptVersion: "p2b-2");

        using var factory = WithFakeClassifier(new FakeAiClassifier(classification));
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfigWithMode(enabled: true, mode: AiMode.SuggestOnly, suggestThreshold: 0.5));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        // SuggestOnly above threshold: suggestion must be returned AND persisted.
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["suggestedNodePath"].Should().NotBeNull();
        json["confidence"]!.GetValue<double>().Should().Be(0.85);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAiSuggestionStore>();
        var saved = await store.GetLatestForConversationAsync(EntityId.From(s_tenantId.Value), convId, CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.SuggestedLeafNodeId.Value.Should().Be(LeafNodeId);
    }

    [Fact]
    public async Task GetTypificationSuggestion_ShouldNotPersist_WhenClassifierReturnsNull()
    {
        using var factory = WithFakeClassifier(new FakeAiClassifier(null));
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfigWithMode(enabled: true, mode: AiMode.SuggestOnly, suggestThreshold: 0.5));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        await AssertEmptySuggestionAsync(response);

        // Null classification → nothing persisted.
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAiSuggestionStore>();
        var saved = await store.GetLatestForConversationAsync(EntityId.From(s_tenantId.Value), convId, CancellationToken.None);
        saved.Should().BeNull("classifier returned null so nothing should have been saved");
    }

    // ─── C1 — server-side confidence-band gating (None / Suggest / AutoFill) ────

    [Fact]
    public async Task GetSuggestion_ShouldReturnBandNone_BelowSuggestThreshold()
    {
        // confidence 0.4 < suggestThreshold 0.7 → Band.None (empty response)
        var classification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string>(),
            Confidence: 0.4,
            Sentiment: "neutral");

        using var factory = WithFakeClassifier(new FakeAiClassifier(classification));
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfigWithModeAndThresholds(
                enabled: true,
                mode: AiMode.SuggestOnly,
                suggestThreshold: 0.7,
                autoApplyThreshold: 0.85));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertEmptySuggestionAsync(response);
    }

    [Fact]
    public async Task GetSuggestion_ShouldReturnBandSuggest_InMidBand()
    {
        // confidence 0.75: above suggestThreshold 0.6 but below autoApplyThreshold 0.85 → Suggest
        var classification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string>(),
            Confidence: 0.75,
            Sentiment: "neutral");

        using var factory = WithFakeClassifier(new FakeAiClassifier(classification));
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfigWithModeAndThresholds(
                enabled: true,
                mode: AiMode.SuggestOnly,
                suggestThreshold: 0.6,
                autoApplyThreshold: 0.85));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["suggestedNodePath"].Should().NotBeNull();
        json["band"]!.GetValue<string>().Should().Be("Suggest");
    }

    [Fact]
    public async Task GetSuggestion_ShouldReturnBandAutoFill_AboveAutoApply_WhenModeAutoFillAndCalibrationReady()
    {
        // confidence 0.92 ≥ autoApplyThreshold 0.85, Mode=AutoFill, calibration ready → AutoFill
        var classification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string>(),
            Confidence: 0.92,
            Sentiment: "neutral");

        using var factory = WithFakeClassifierAndCalibration(
            new FakeAiClassifier(classification),
            new FakeCalibration(autoFillReady: true));
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfigWithModeAndThresholds(
                enabled: true,
                mode: AiMode.AutoFill,
                suggestThreshold: 0.6,
                autoApplyThreshold: 0.85));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["suggestedNodePath"].Should().NotBeNull();
        json["band"]!.GetValue<string>().Should().Be("AutoFill");
    }

    [Fact]
    public async Task GetSuggestion_ShouldPersistSurfacedBand_MatchingResponseBand()
    {
        // Suggest path: mid-band confidence → response Band == "Suggest" and the persisted
        // record's SurfacedBand must equal it (the band is decided BEFORE save in C1).
        var suggestClassification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string>(),
            Confidence: 0.75,
            Sentiment: "neutral");

        using var suggestFactory = WithFakeClassifier(new FakeAiClassifier(suggestClassification));
        using var suggestClient = AuthenticatedClient(suggestFactory);
        var suggestConvId = await SeedSchemaAndConversationAsync(
            suggestFactory, AiConfigWithModeAndThresholds(
                enabled: true, mode: AiMode.SuggestOnly, suggestThreshold: 0.6, autoApplyThreshold: 0.85));

        var suggestResponse = await suggestClient.PostAsync(
            $"/api/conversations/{suggestConvId.Value}/typification-suggestion", content: null);
        suggestResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var suggestJson = JsonNode.Parse(await suggestResponse.Content.ReadAsStringAsync());
        suggestJson!["band"]!.GetValue<string>().Should().Be("Suggest");

        using (var scope = suggestFactory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAiSuggestionStore>();
            var saved = await store.GetLatestForConversationAsync(
                EntityId.From(s_tenantId.Value), suggestConvId, CancellationToken.None);
            saved.Should().NotBeNull();
            saved!.SurfacedBand.Should().Be(TypificationBand.Suggest);
        }

        // AutoFill path: high confidence + Mode=AutoFill + calibration ready → response Band ==
        // "AutoFill" and the persisted record's SurfacedBand must equal it.
        var autoFillClassification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string>(),
            Confidence: 0.92,
            Sentiment: "neutral");

        using var autoFillFactory = WithFakeClassifierAndCalibration(
            new FakeAiClassifier(autoFillClassification),
            new FakeCalibration(autoFillReady: true));
        using var autoFillClient = AuthenticatedClient(autoFillFactory);
        var autoFillConvId = await SeedSchemaAndConversationAsync(
            autoFillFactory, AiConfigWithModeAndThresholds(
                enabled: true, mode: AiMode.AutoFill, suggestThreshold: 0.6, autoApplyThreshold: 0.85));

        var autoFillResponse = await autoFillClient.PostAsync(
            $"/api/conversations/{autoFillConvId.Value}/typification-suggestion", content: null);
        autoFillResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var autoFillJson = JsonNode.Parse(await autoFillResponse.Content.ReadAsStringAsync());
        autoFillJson!["band"]!.GetValue<string>().Should().Be("AutoFill");

        using (var scope = autoFillFactory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAiSuggestionStore>();
            var saved = await store.GetLatestForConversationAsync(
                EntityId.From(s_tenantId.Value), autoFillConvId, CancellationToken.None);
            saved.Should().NotBeNull();
            saved!.SurfacedBand.Should().Be(TypificationBand.AutoFill);
        }
    }

    [Fact]
    public async Task GetSuggestion_ShouldDowngradeToSuggest_WhenAboveAutoApplyButCalibrationNotReady()
    {
        // confidence 0.92 ≥ autoApplyThreshold 0.85, Mode=AutoFill, BUT calibration NOT ready → Suggest
        var classification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string>(),
            Confidence: 0.92,
            Sentiment: "neutral");

        using var factory = WithFakeClassifierAndCalibration(
            new FakeAiClassifier(classification),
            new FakeCalibration(autoFillReady: false));
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfigWithModeAndThresholds(
                enabled: true,
                mode: AiMode.AutoFill,
                suggestThreshold: 0.6,
                autoApplyThreshold: 0.85));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["suggestedNodePath"].Should().NotBeNull();
        json["band"]!.GetValue<string>().Should().Be("Suggest");
    }

    [Fact]
    public async Task GetSuggestion_ShouldDowngradeToSuggest_WhenModeIsSuggestOnly_EvenAboveAutoApply()
    {
        // confidence 0.92 ≥ autoApplyThreshold 0.85, but Mode=SuggestOnly → always Suggest (never AutoFill)
        var classification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string>(),
            Confidence: 0.92,
            Sentiment: "neutral");

        // No need to stub calibration — the mode guard short-circuits before the calibration call.
        using var factory = WithFakeClassifier(new FakeAiClassifier(classification));
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfigWithModeAndThresholds(
                enabled: true,
                mode: AiMode.SuggestOnly,
                suggestThreshold: 0.6,
                autoApplyThreshold: 0.85));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["suggestedNodePath"].Should().NotBeNull();
        json["band"]!.GetValue<string>().Should().Be("Suggest");
    }

    [Fact]
    public async Task GetSuggestion_ShouldReturnEmpty_WhenModeIsOff()
    {
        // Mode=Off overrides Enabled=true + high confidence → empty (Band.None authority)
        var classification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string>(),
            Confidence: 0.99,
            Sentiment: "positive");

        using var factory = WithFakeClassifier(new FakeAiClassifier(classification));
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfigWithModeAndThresholds(
                enabled: true,
                mode: AiMode.Off,
                suggestThreshold: 0.0,
                autoApplyThreshold: 0.0));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertEmptySuggestionAsync(response);
    }

    // ─── E1 — effective config (binding override wins over schema AiConfig) ──────

    [Fact]
    public async Task Suggestion_ShouldHonorBindingOverride_WhenOverrideDisablesAi()
    {
        // End-to-end E1 proof: the SCHEMA's AiConfig is enabled + SuggestOnly (would otherwise
        // surface a high-confidence suggestion), but the BINDING's AiConfigOverride sets Mode=Off.
        // The suggestion handler reads resolved.EffectiveAiConfig (override ?? schema), so it must
        // see Mode=Off and return the empty suggestion — proving the consumer reads the EFFECTIVE
        // config, NOT Schema.AiConfig. If it regressed to Schema.AiConfig, a suggestion would surface.
        var classification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string>(),
            Confidence: 0.99,
            Sentiment: "positive");

        using var factory = WithFakeClassifier(new FakeAiClassifier(classification));
        using var client = AuthenticatedClient(factory);

        var convId = await SeedSchemaAndConversationAsync(
            factory,
            // Schema AiConfig: enabled + SuggestOnly (would surface) — proves the override, not the
            // schema, is what disables AI here.
            AiConfigWithMode(enabled: true, mode: AiMode.SuggestOnly, suggestThreshold: 0.5),
            // Binding override: AI Off → EffectiveAiConfig.Mode == Off → empty suggestion.
            bindingAiConfigOverride: AiConfigWithMode(enabled: true, mode: AiMode.Off, suggestThreshold: 0.5));

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertEmptySuggestionAsync(response);
    }

    // ─── E3 — per-tenant daily LLM token budget (fail-closed) ───────────────────

    [Fact]
    public async Task Suggestion_ShouldDegradeToEmpty_WhenDailyTokenBudgetExceeded()
    {
        // The classifier WOULD return a high-confidence suggestion, but the tenant is already
        // over its daily token budget → fail-closed degrade to the empty suggestion, the LLM is
        // never called, the budget_exceeded counter increments, and an audit entry is written.
        var classification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string>(),
            Confidence: 0.99,
            Sentiment: "positive",
            ModelId: "test-model",
            PromptVersion: "p2b-3",
            Usage: new Verbara.Platform.Llm.LlmUsage(100, 50, 150));

        var fake = new FakeAiClassifier(classification);
        using var factory = WithFakeClassifier(fake);
        using var client = AuthenticatedClient(factory);

        // DailyTokenBudget = 1000; pre-seed the shared budget singleton so the tenant is already over.
        const long budget = 1000;
        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfigWithBudget(enabled: true, mode: AiMode.SuggestOnly, suggestThreshold: 0.5, dailyTokenBudget: budget));

        using (var seedScope = factory.Services.CreateScope())
        {
            var tokenBudget = seedScope.ServiceProvider.GetRequiredService<ITypificationTokenBudget>();
            await tokenBudget.RecordUsageAsync(s_tenantId, budget, CancellationToken.None);
        }

        using var listener = new CountingMeterListener(TypificationAiMetrics.MeterName, "suggestion.budget_exceeded");

        var response = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertEmptySuggestionAsync(response);

        // The LLM (classifier) must NOT have been called — the budget gate short-circuits before classify.
        fake.CallCount.Should().Be(0, "the budget gate degrades BEFORE the expensive classify call");

        // The fail-closed counter must have incremented.
        listener.Total.Should().Be(1);

        // An audit entry must have been written.
        using var scope = factory.Services.CreateScope();
        var auditStore = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var hits = await auditStore.SearchAsync(
            s_tenantId,
            new AuditQuery(Action: "typification.ai.budget_exceeded", Page: 1, PageSize: 100),
            CancellationToken.None);
        hits.Items.Should().ContainSingle(e =>
            e.TargetId == convId.Value
            && e.Category == "config"
            && e.ActorType == "system"
            && e.Metadata != null
            && e.Metadata["dailyTokenBudget"] == budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Suggestion_ShouldRecordUsageAndAllow_WhenUnderBudget()
    {
        // First call: under budget → a normal suggestion is returned AND the call's token usage is
        // recorded against the tenant's running UTC-day sum. A budget tiny enough that the FIRST
        // call's recorded usage pushes the tenant over means a SECOND call must fail-closed —
        // proving RecordUsageAsync actually moved the accumulator.
        var classification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string>(),
            Confidence: 0.9,
            Sentiment: "positive",
            ModelId: "test-model",
            PromptVersion: "p2b-3",
            Usage: new Verbara.Platform.Llm.LlmUsage(80, 20, 100));

        var fake = new FakeAiClassifier(classification);
        using var factory = WithFakeClassifier(fake);
        using var client = AuthenticatedClient(factory);

        // Budget 100, classifier reports 100 tokens: first call is under (sum starts at 0), the
        // recorded 100 then reaches the budget so the second call degrades.
        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfigWithBudget(enabled: true, mode: AiMode.SuggestOnly, suggestThreshold: 0.5, dailyTokenBudget: 100));

        var first = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstJson = JsonNode.Parse(await first.Content.ReadAsStringAsync());
        firstJson!["suggestedNodePath"].Should().NotBeNull("the first call is under budget so a suggestion is returned");
        fake.CallCount.Should().Be(1, "the first call actually invokes the classifier");

        var second = await client.PostAsync(
            $"/api/conversations/{convId.Value}/typification-suggestion", content: null);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertEmptySuggestionAsync(second);
        fake.CallCount.Should().Be(1, "the second call is over budget so the classifier is NOT called again");
    }

    // ─── E3 — dedicated per-tenant "llm" rate-limit policy ──────────────────────

    /// <summary>The OLD punitive "__global__" fallback permit (5) — used here purely as a lower-bound
    /// sentinel. Proving a single tenant's burst yields strictly MORE than this rules out the old
    /// "everything collapses to a 5/min __global__ bucket" bug, regardless of the current fallback
    /// value (raised to <see cref="TenantRateLimitPolicy.GlobalFallbackPermitLimit"/> == 30 by the
    /// E3 review hedge). The per-tenant bucket under test is <see cref="TenantRateLimitPolicy.LlmPermitLimit"/> (30).</summary>
    private const int OldPunitiveGlobalFallback = 5;

    [Fact]
    public async Task Suggestion_ShouldRateLimit_PerTenant()
    {
        // The suggestion route carries the dedicated "llm" policy (PermitLimit = 30 / 1-min sliding
        // window), partitioned per tenant by the X-Tenant-Id request header. Firing more than the
        // limit from one tenant must eventually yield a 429.
        //
        // NOTE on tiers: the base factory's tenant is on the ENTERPRISE tier (permit 1200), NOT
        // Unlimited. The 429 observed here therefore comes from the per-tenant "llm" 30/min bucket,
        // not from the generic "per-tenant" limiter (Enterprise would allow 1200, and that limiter
        // is also currently a no-op — see the pre-existing flaw documented in TenantRateLimitPolicy).
        // Critically, the success count proves the PER-TENANT 30 bucket and NOT the old global 5
        // bucket: before Fix 1, Items["TenantId"] was unset at the UseRateLimiter() position so every
        // request collapsed into "__global__" (permit 5). Reading X-Tenant-Id directly gives this
        // tenant its own 30/min partition.
        var classification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string>(),
            Confidence: 0.9,
            Sentiment: "positive");

        using var factory = WithFakeClassifier(new FakeAiClassifier(classification));
        using var client = AuthenticatedClient(factory); // sends X-Tenant-Id: TestTenantId

        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfig(enabled: true, suggestThreshold: 0.5));

        var url = $"/api/conversations/{convId.Value}/typification-suggestion";

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < TenantRateLimitPolicy.LlmPermitLimit + 10; i++)
        {
            var resp = await client.PostAsync(url, content: null);
            statuses.Add(resp.StatusCode);
        }

        // The "llm" policy throttles this tenant within a single window. Firing well past the permit
        // limit MUST surface a 429 (proving the policy is wired + applied per-tenant).
        statuses.Should().Contain(HttpStatusCode.TooManyRequests,
            "more than the per-tenant llm permit limit was fired in one window");
        statuses.Should().Contain(HttpStatusCode.OK,
            "requests at the start of the window succeed before the limit is hit");

        var okCount = statuses.Count(s => s == HttpStatusCode.OK);
        okCount.Should().BeLessThanOrEqualTo(TenantRateLimitPolicy.LlmPermitLimit,
            "no more than the per-tenant permit limit (30) may succeed inside one sliding window");
        okCount.Should().BeGreaterThan(OldPunitiveGlobalFallback,
            "the per-tenant 30 bucket (not the old punitive global 5 bucket) must absorb the burst — "
            + "proving the limiter partitions on X-Tenant-Id, not the unset Items[\"TenantId\"]");
    }

    [Fact]
    public async Task Suggestion_ShouldNotShareRateLimitBucket_AcrossTenants()
    {
        // Per-tenant ISOLATION: after tenant A trips its own "llm" 30/min 429, a single request for a
        // DIFFERENT tenant B (different X-Tenant-Id) must NOT be 429 — it has its own partition.
        //
        // B is intentionally unseeded (no customer-tenant / feature-gate / license rows), so once the
        // request clears the rate limiter it hits the later operational-tenant + license gates and
        // returns THEIR status (e.g. 409/402/401), never 200. That's fine: the assertion is precisely
        // "B is NOT 429", which proves B did not inherit A's exhausted bucket. Asserting 200 for B
        // would require fully seeding B and is not needed to prove isolation.
        var classification = new AiClassification(
            NodePath: [EntityId.From(RootNodeId), EntityId.From(LeafNodeId)],
            FieldValues: new Dictionary<string, string>(),
            Confidence: 0.9,
            Sentiment: "positive");

        using var factory = WithFakeClassifier(new FakeAiClassifier(classification));
        using var clientA = AuthenticatedClient(factory); // X-Tenant-Id: TestTenantId (tenant A)

        var convId = await SeedSchemaAndConversationAsync(
            factory, AiConfig(enabled: true, suggestThreshold: 0.5));
        var url = $"/api/conversations/{convId.Value}/typification-suggestion";

        // Exhaust A's per-tenant llm bucket until a 429 appears.
        var sawTenantA429 = false;
        for (var i = 0; i < TenantRateLimitPolicy.LlmPermitLimit + 10 && !sawTenantA429; i++)
        {
            var resp = await clientA.PostAsync(url, content: null);
            sawTenantA429 = resp.StatusCode == HttpStatusCode.TooManyRequests;
        }

        sawTenantA429.Should().BeTrue("tenant A must trip its own per-tenant llm 429 before the isolation check");

        // A single request for tenant B (its own X-Tenant-Id) — same factory, same route — must land
        // in B's own (empty) partition and therefore NOT be throttled.
        using var clientB = factory.CreateClient();
        clientB.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
        clientB.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant-isolation-B");

        var tenantBResponse = await clientB.PostAsync(url, content: null);

        tenantBResponse.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
            "tenant B has its own llm partition and must not inherit tenant A's exhausted 30/min bucket");
    }

    // ─── D2 — typify provenance (B3 — server-authoritative) ─────────────────────

    /// <summary>
    /// Verifies B3 server-authoritative derivation: a stored AI suggestion with the same
    /// leaf the agent commits → <c>Source=AutoAi</c>, <c>AiAccepted=true</c>.
    /// Client-supplied AI flags are intentionally ignored by the handler.
    /// </summary>
    [Fact]
    public async Task Typify_ShouldRecordAutoAiProvenance_WhenStoredSuggestionMatchesCommittedLeaf()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var convId = await SeedSchemaAndConversationAsync(
            _factory, AiConfig(enabled: false, suggestThreshold: 0.0));

        // Seed a stored suggestion: AI suggested the same leaf the agent is about to commit.
        using (var scope = _factory.Services.CreateScope())
        {
            var suggestionStore = scope.ServiceProvider.GetRequiredService<IAiSuggestionStore>();
            await suggestionStore.SaveAsync(new AiSuggestionRecord
            {
                Id = EntityId.New(),
                TenantId = EntityId.From(AuthenticatedPlatformApiFactory.TestTenantId),
                ConversationId = convId,
                SchemaId = EntityId.New(),
                SchemaVersion = 1,
                SuggestedLeafNodeId = EntityId.From(LeafNodeId),
                SuggestedNodePath = [RootNodeId, LeafNodeId],
                SuggestedFieldValues = new Dictionary<string, string>(),
                Confidence = 0.88,
                ModelId = "gpt-4o-mini",
                PromptVersion = "v1",
                SurfacedBand = TypificationBand.Suggest,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }

        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string>(),
            notes = "ai accepted",
            // Client flags are IGNORED by B3 — server derives from stored suggestion.
            aiAccepted = false,
            aiSuggested = false,
        });

        var response = await client.PostAsync($"/api/conversations/{convId.Value}/typify", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["source"]!.GetValue<string>().Should().Be("AutoAi");

        using var scope2 = _factory.Services.CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ITypificationSubmissionStore>();
        var saved = await store.GetByConversationIdAsync(s_tenantId, convId, CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.AiSuggested.Should().BeTrue();
        saved.AiConfidence.Should().Be(0.88);
        saved.AiAccepted.Should().BeTrue();
        saved.Source.Should().Be(SubmissionSource.AutoAi);
    }

    [Fact]
    public async Task Typify_ShouldRecordManual_WhenNoAi()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var convId = await SeedSchemaAndConversationAsync(
            _factory, AiConfig(enabled: false, suggestThreshold: 0.0));

        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string>(),
            notes = "manual",
        });

        var response = await client.PostAsync($"/api/conversations/{convId.Value}/typify", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["source"]!.GetValue<string>().Should().Be("Manual");

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITypificationSubmissionStore>();
        var saved = await store.GetByConversationIdAsync(s_tenantId, convId, CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.AiSuggested.Should().BeFalse();
        saved.AiConfidence.Should().BeNull();
        saved.Source.Should().Be(SubmissionSource.Manual);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static async Task AssertEmptySuggestionAsync(HttpResponseMessage response)
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        // All members omitted when null (DefaultIgnoreCondition.WhenWritingNull).
        json!["suggestedNodePath"].Should().BeNull();
        json["suggestedFieldValues"].Should().BeNull();
        json["confidence"].Should().BeNull();
        json["sentiment"].Should().BeNull();
        // band serializes as "None" (non-null enum value; WhenWritingNull does not suppress it).
        json["band"]!.GetValue<string>().Should().Be("None");
    }

    private static TypificationAiConfig AiConfig(
        bool enabled, double suggestThreshold, bool sentimentGating = false) =>
        new()
        {
            Enabled = enabled,
            Mode = AiMode.SuggestOnly,
            SuggestThreshold = suggestThreshold,
            SentimentGating = sentimentGating,
            EntityFieldMap = new Dictionary<string, string>(),
        };

    private static TypificationAiConfig AiConfigWithMode(
        bool enabled, AiMode mode, double suggestThreshold, bool sentimentGating = false) =>
        new()
        {
            Enabled = enabled,
            Mode = mode,
            SuggestThreshold = suggestThreshold,
            SentimentGating = sentimentGating,
            EntityFieldMap = new Dictionary<string, string>(),
        };

    private static TypificationAiConfig AiConfigWithModeAndThresholds(
        bool enabled, AiMode mode, double suggestThreshold, double autoApplyThreshold) =>
        new()
        {
            Enabled = enabled,
            Mode = mode,
            SuggestThreshold = suggestThreshold,
            AutoApplyThreshold = autoApplyThreshold,
            SentimentGating = false,
            EntityFieldMap = new Dictionary<string, string>(),
        };

    private static TypificationAiConfig AiConfigWithBudget(
        bool enabled, AiMode mode, double suggestThreshold, long dailyTokenBudget) =>
        new()
        {
            Enabled = enabled,
            Mode = mode,
            SuggestThreshold = suggestThreshold,
            SentimentGating = false,
            DailyTokenBudget = dailyTokenBudget,
            EntityFieldMap = new Dictionary<string, string>(),
        };

    private WebApplicationFactory<Program> WithFakeClassifier(FakeAiClassifier fake) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(ITypificationAiClassifier))
                    .ToList();
                foreach (var d in existing) services.Remove(d);
                services.AddSingleton<ITypificationAiClassifier>(fake);
            }));

    private WebApplicationFactory<Program> WithFakeClassifierAndCalibration(
        FakeAiClassifier fakeClassifier, FakeCalibration fakeCalibration) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                // Replace classifier
                var existingClassifier = services
                    .Where(d => d.ServiceType == typeof(ITypificationAiClassifier))
                    .ToList();
                foreach (var d in existingClassifier) services.Remove(d);
                services.AddSingleton<ITypificationAiClassifier>(fakeClassifier);

                // Replace calibration
                var existingCalibration = services
                    .Where(d => d.ServiceType == typeof(ITypificationCalibration))
                    .ToList();
                foreach (var d in existingCalibration) services.Remove(d);
                services.AddSingleton<ITypificationCalibration>(fakeCalibration);
            }));

    private static HttpClient AuthenticatedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", AuthenticatedPlatformApiFactory.TestTenantId);
        return client;
    }

    /// <summary>
    /// Seeds a published schema (with the supplied <paramref name="aiConfig"/>) + tenant-wide
    /// binding + an Active conversation directly into the factory's in-memory stores, and
    /// returns the conversation id. A required field is intentionally omitted — the schema
    /// has one Success-category leaf so the sentiment gate has something to suppress.
    /// <para>
    /// When <paramref name="bindingAiConfigOverride"/> is non-null it is stored on the binding's
    /// <see cref="SchemaBinding.AiConfigOverride"/> so the resolver's <c>EffectiveAiConfig</c>
    /// becomes the override (not the schema's AiConfig) — exercising the E1 effective-config path.
    /// </para>
    /// </summary>
    private static async Task<EntityId> SeedSchemaAndConversationAsync(
        WebApplicationFactory<Program> factory,
        TypificationAiConfig aiConfig,
        TypificationAiConfig? bindingAiConfigOverride = null)
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
                Name = "AI Suggestion Schema",
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
                AiConfigOverride = bindingAiConfigOverride,
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
    /// Test double for <see cref="ITypificationAiClassifier"/> that returns a canned
    /// classification (or null) without touching an LLM provider, so the endpoint's
    /// confidence/sentiment gates can be exercised deterministically.
    /// </summary>
    private sealed class FakeAiClassifier : ITypificationAiClassifier
    {
        private readonly AiClassification? _result;

        public FakeAiClassifier(AiClassification? result) => _result = result;

        /// <summary>Number of times <see cref="ClassifyAsync"/> was invoked (E3 — proves the budget gate skips the LLM call).</summary>
        public int CallCount { get; private set; }

        public Task<AiClassification?> ClassifyAsync(
            TypificationSchema schema,
            EntityId? subtreeRoot,
            Conversation conversation,
            IReadOnlyList<Message> transcript,
            CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_result);
        }
    }

    /// <summary>
    /// Test double for <see cref="ITypificationCalibration"/> that returns a canned
    /// <see cref="CalibrationStatus"/> so the C1 AutoFill band gate can be exercised
    /// deterministically without requiring reconciled sample data in the store.
    /// </summary>
    private sealed class FakeCalibration : ITypificationCalibration
    {
        private readonly bool _autoFillReady;

        public FakeCalibration(bool autoFillReady) => _autoFillReady = autoFillReady;

        public Task<CalibrationStatus> GetStatusAsync(
            TenantId tenantId, EntityId schemaId, CancellationToken ct) =>
            Task.FromResult(new CalibrationStatus(
                Samples: _autoFillReady ? 300 : 10,
                Accuracy: _autoFillReady ? 0.90 : 0.50,
                AutoFillReady: _autoFillReady,
                AutonomousReady: false));
    }

    /// <summary>
    /// Minimal <see cref="System.Diagnostics.Metrics.MeterListener"/> that sums measurements for a
    /// single named long-counter instrument on a given meter (E3 — asserts the
    /// <c>suggestion.budget_exceeded</c> counter incremented).
    /// </summary>
    private sealed class CountingMeterListener : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener;
        private readonly string _meterName;
        private readonly string _instrumentName;
        private long _total;

        public CountingMeterListener(string meterName, string instrumentName)
        {
            _meterName = meterName;
            _instrumentName = instrumentName;
            _listener = new System.Diagnostics.Metrics.MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == _meterName && instrument.Name == _instrumentName)
                        listener.EnableMeasurementEvents(instrument);
                },
            };
            _listener.SetMeasurementEventCallback<long>(
                (_, measurement, _, _) => Interlocked.Add(ref _total, measurement));
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }
}

/// <summary>
/// Variant of <see cref="AuthenticatedPlatformApiFactory"/> that licenses EXACTLY
/// <see cref="LicenseFeature.AdvancedTypification"/> (and NOT
/// <see cref="LicenseFeature.TypificationAi"/>) so the combined-flags gate on
/// <c>POST /conversations/{id}/typification-suggestion</c> drives the 402 path. Mirrors
/// <c>NoTypificationLicenseFactory</c>'s wiring; only the licensed feature set differs.
/// </summary>
public sealed class AdvancedTypificationOnlyLicenseFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "test-api-key-12345";
    public const string TestTenantId = "tenant-test-001";
    public const string TestUserId = "test-admin-user";

    private static readonly string s_hashedKey = HashKey(TestApiKey);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            AuthenticatedPlatformApiFactory.SetupTestAuth(services, s_hashedKey, TestTenantId, TestUserId);
            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);

            // The only difference vs AuthenticatedPlatformApiFactory: license EXACTLY
            // AdvancedTypification (no TypificationAi) so the suggestion gate 402s.
            services.AddExactProFeaturesLicensed(LicenseFeature.AdvancedTypification);
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
        });

        var host = base.CreateHost(builder);

        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, TestTenantId);
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
