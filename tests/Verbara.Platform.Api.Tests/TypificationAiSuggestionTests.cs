using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
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
    /// </summary>
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

        public Task<AiClassification?> ClassifyAsync(
            TypificationSchema schema,
            EntityId? subtreeRoot,
            Conversation conversation,
            IReadOnlyList<Message> transcript,
            CancellationToken ct) => Task.FromResult(_result);
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
