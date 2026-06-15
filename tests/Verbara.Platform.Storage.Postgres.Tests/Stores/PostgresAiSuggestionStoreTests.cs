using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Typification.Ai;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Round-trips <see cref="PostgresAiSuggestionStore"/> against a real Postgres DB via
/// Testcontainers so JSONB persistence (suggested_node_path, suggested_field_values),
/// nullable reconciliation fields (committed_leaf_node_id, accepted), and the
/// accuracy aggregation query are exercised end-to-end.
/// </summary>
public sealed class PostgresAiSuggestionStoreTests : IClassFixture<AiSuggestionStoreFixture>, IAsyncLifetime
{
    private readonly AiSuggestionStoreFixture _fixture;
    private readonly PostgresAiSuggestionStore _store;
    private static readonly EntityId Tenant = EntityId.From("acme-ai");
    private static readonly EntityId SchemaId = EntityId.From("schema-ai-1");

    public PostgresAiSuggestionStoreTests(AiSuggestionStoreFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresAiSuggestionStore(_fixture.DataSource);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static AiSuggestionRecord NewRecord(
        EntityId? conversationId = null,
        EntityId? schemaId = null,
        double confidence = 0.9,
        DateTimeOffset? createdAt = null,
        string? sentiment = null) => new()
    {
        Id = EntityId.New(),
        TenantId = Tenant,
        ConversationId = conversationId ?? EntityId.New(),
        SchemaId = schemaId ?? SchemaId,
        SchemaVersion = 2,
        SuggestedLeafNodeId = EntityId.New(),
        SuggestedNodePath = ["root", "child", "leaf"],
        SuggestedFieldValues = new Dictionary<string, string>
        {
            ["amount"] = "1500",
            ["reason"] = "upsell",
        },
        Confidence = confidence,
        Sentiment = sentiment,
        ModelId = "gpt-4o-mini",
        PromptVersion = "v1.2",
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task SaveAndGetLatest_ShouldReturnMostRecent_WhenMultipleSuggestions()
    {
        var conversationId = EntityId.New();
        var older = NewRecord(conversationId: conversationId,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var newer = NewRecord(conversationId: conversationId,
            createdAt: DateTimeOffset.UtcNow);

        await _store.SaveAsync(older, CancellationToken.None);
        await _store.SaveAsync(newer, CancellationToken.None);

        var result = await _store.GetLatestForConversationAsync(
            Tenant, conversationId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be(newer.Id);
        result.SuggestedNodePath.Should().Equal("root", "child", "leaf");
        result.SuggestedFieldValues.Should().HaveCount(2);
        result.SuggestedFieldValues["amount"].Should().Be("1500");
        result.SuggestedFieldValues["reason"].Should().Be("upsell");
        result.Confidence.Should().BeApproximately(0.9, 1e-9);
        result.ModelId.Should().Be("gpt-4o-mini");
        result.PromptVersion.Should().Be("v1.2");
    }

    [Fact]
    public async Task GetLatest_ShouldReturnNull_WhenNone()
    {
        var result = await _store.GetLatestForConversationAsync(
            Tenant, EntityId.New(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task MarkReconciled_ShouldSetCommittedAndAccepted()
    {
        var record = NewRecord();
        await _store.SaveAsync(record, CancellationToken.None);
        var committedLeaf = EntityId.New();

        await _store.MarkReconciledAsync(record.Id, committedLeaf, accepted: true, CancellationToken.None);

        var loaded = await _store.GetLatestForConversationAsync(
            Tenant, record.ConversationId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.CommittedLeafNodeId.Should().Be(committedLeaf);
        loaded.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task QueryAccuracy_ShouldReturnSamplesAndAcceptRate_WhenReconciledAboveThreshold()
    {
        // 3 reconciled above threshold: 2 accepted, 1 rejected
        var r1 = NewRecord(confidence: 0.9);
        var r2 = NewRecord(confidence: 0.85);
        var r3 = NewRecord(confidence: 0.8);
        // 1 below threshold — excluded
        var r4 = NewRecord(confidence: 0.5);
        // 1 not reconciled — excluded
        var r5 = NewRecord(confidence: 0.95);

        await _store.SaveAsync(r1, CancellationToken.None);
        await _store.SaveAsync(r2, CancellationToken.None);
        await _store.SaveAsync(r3, CancellationToken.None);
        await _store.SaveAsync(r4, CancellationToken.None);
        await _store.SaveAsync(r5, CancellationToken.None);

        await _store.MarkReconciledAsync(r1.Id, EntityId.New(), accepted: true, CancellationToken.None);
        await _store.MarkReconciledAsync(r2.Id, EntityId.New(), accepted: true, CancellationToken.None);
        await _store.MarkReconciledAsync(r3.Id, EntityId.New(), accepted: false, CancellationToken.None);
        await _store.MarkReconciledAsync(r4.Id, EntityId.New(), accepted: true, CancellationToken.None);

        var (samples, acceptRate) = await _store.QueryAccuracyAsync(
            Tenant, SchemaId, confidenceThreshold: 0.75, CancellationToken.None);

        samples.Should().Be(3);
        acceptRate.Should().BeApproximately(2.0 / 3.0, 1e-9);
    }

    [Fact]
    public async Task QueryAccuracy_ShouldExcludeBelowThreshold()
    {
        var r1 = NewRecord(confidence: 0.4);
        var r2 = NewRecord(confidence: 0.6);
        await _store.SaveAsync(r1, CancellationToken.None);
        await _store.SaveAsync(r2, CancellationToken.None);
        await _store.MarkReconciledAsync(r1.Id, EntityId.New(), accepted: true, CancellationToken.None);
        await _store.MarkReconciledAsync(r2.Id, EntityId.New(), accepted: false, CancellationToken.None);

        var (samples, acceptRate) = await _store.QueryAccuracyAsync(
            Tenant, SchemaId, confidenceThreshold: 0.75, CancellationToken.None);

        samples.Should().Be(0);
        acceptRate.Should().Be(0d);
    }

    [Fact]
    public async Task SaveAndGetLatest_ShouldRoundTripNullableSentiment_WhenNull()
    {
        var record = NewRecord(sentiment: null);
        await _store.SaveAsync(record, CancellationToken.None);

        var loaded = await _store.GetLatestForConversationAsync(
            Tenant, record.ConversationId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Sentiment.Should().BeNull();
        loaded.CommittedLeafNodeId.Should().BeNull();
        loaded.Accepted.Should().BeNull();
    }

    [Fact]
    public async Task SaveAndGetLatest_ShouldRoundTripSentiment_WhenPresent()
    {
        var record = NewRecord(sentiment: "positive");
        await _store.SaveAsync(record, CancellationToken.None);

        var loaded = await _store.GetLatestForConversationAsync(
            Tenant, record.ConversationId, CancellationToken.None);

        loaded!.Sentiment.Should().Be("positive");
    }
}
