using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Ai;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryAiSuggestionStoreTests
{
    private static readonly EntityId Tenant = EntityId.From("tenant-ai-1");
    private static readonly EntityId SchemaId = EntityId.From("schema-x");

    private static AiSuggestionRecord MakeRecord(
        EntityId? conversationId = null,
        EntityId? schemaId = null,
        int schemaVersion = 1,
        double confidence = 0.9,
        DateTimeOffset? createdAt = null,
        bool? accepted = null,
        EntityId? committedLeafNodeId = null,
        TypificationBand surfacedBand = TypificationBand.Suggest) => new()
    {
        Id = EntityId.New(),
        TenantId = Tenant,
        ConversationId = conversationId ?? EntityId.New(),
        SchemaId = schemaId ?? SchemaId,
        SchemaVersion = schemaVersion,
        SuggestedLeafNodeId = EntityId.New(),
        SuggestedNodePath = ["root", "child"],
        SuggestedFieldValues = new Dictionary<string, string> { ["key"] = "val" },
        Confidence = confidence,
        Sentiment = null,
        ModelId = "gpt-4o-mini",
        PromptVersion = "v1",
        SurfacedBand = surfacedBand,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        CommittedLeafNodeId = committedLeafNodeId,
        Accepted = accepted,
    };

    [Fact]
    public async Task SaveAndGetLatest_ShouldReturnMostRecent_WhenMultipleSuggestions()
    {
        var store = new InMemoryAiSuggestionStore();
        var conversationId = EntityId.New();
        var older = MakeRecord(conversationId: conversationId, createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var newer = MakeRecord(conversationId: conversationId, createdAt: DateTimeOffset.UtcNow);

        await store.SaveAsync(older, CancellationToken.None);
        await store.SaveAsync(newer, CancellationToken.None);

        var result = await store.GetLatestForConversationAsync(Tenant, conversationId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be(newer.Id);
        result.CreatedAt.Should().Be(newer.CreatedAt);
    }

    [Fact]
    public async Task GetLatest_ShouldReturnNull_WhenNone()
    {
        var store = new InMemoryAiSuggestionStore();

        var result = await store.GetLatestForConversationAsync(
            Tenant, EntityId.New(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatest_ShouldNotReturnOtherTenantRecord_WhenTenantDiffers()
    {
        var store = new InMemoryAiSuggestionStore();
        var conversationId = EntityId.New();
        var otherTenantRecord = new AiSuggestionRecord
        {
            Id = EntityId.New(),
            TenantId = EntityId.From("tenant-other"),
            ConversationId = conversationId,
            SchemaId = SchemaId,
            SchemaVersion = 1,
            SuggestedLeafNodeId = EntityId.New(),
            SuggestedNodePath = [],
            SuggestedFieldValues = new Dictionary<string, string>(),
            Confidence = 0.8,
            ModelId = "gpt-4o-mini",
            PromptVersion = "v1",
            SurfacedBand = TypificationBand.Suggest,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(otherTenantRecord, CancellationToken.None);

        var result = await store.GetLatestForConversationAsync(
            Tenant, conversationId, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task MarkReconciled_ShouldSetCommittedAndAccepted()
    {
        var store = new InMemoryAiSuggestionStore();
        var record = MakeRecord();
        await store.SaveAsync(record, CancellationToken.None);
        var committedLeaf = EntityId.New();

        await store.MarkReconciledAsync(record.Id, committedLeaf, accepted: true, CancellationToken.None);

        var updated = await store.GetLatestForConversationAsync(
            Tenant, record.ConversationId, CancellationToken.None);
        updated.Should().NotBeNull();
        updated!.CommittedLeafNodeId.Should().Be(committedLeaf);
        updated.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task MarkReconciled_ShouldSetAcceptedFalse_WhenRejected()
    {
        var store = new InMemoryAiSuggestionStore();
        var record = MakeRecord();
        await store.SaveAsync(record, CancellationToken.None);

        await store.MarkReconciledAsync(record.Id, EntityId.New(), accepted: false, CancellationToken.None);

        var updated = await store.GetLatestForConversationAsync(
            Tenant, record.ConversationId, CancellationToken.None);
        updated!.Accepted.Should().BeFalse();
    }

    [Fact]
    public async Task QueryAccuracy_ShouldReturnSamplesAndAcceptRate_WhenReconciledAboveThreshold()
    {
        var store = new InMemoryAiSuggestionStore();
        // 3 reconciled above threshold: 2 accepted, 1 rejected
        var r1 = MakeRecord(confidence: 0.9, accepted: true);
        var r2 = MakeRecord(confidence: 0.85, accepted: true);
        var r3 = MakeRecord(confidence: 0.8, accepted: false);
        // 1 reconciled BELOW threshold — must be excluded
        var r4 = MakeRecord(confidence: 0.5, accepted: true);
        // 1 not reconciled — must be excluded
        var r5 = MakeRecord(confidence: 0.95);

        foreach (var r in new[] { r1, r2, r3, r4, r5 })
            await store.SaveAsync(r, CancellationToken.None);

        var (samples, acceptRate) = await store.QueryAccuracyAsync(
            Tenant, SchemaId, schemaVersion: 1, confidenceThreshold: 0.75, CancellationToken.None);

        samples.Should().Be(3);
        acceptRate.Should().BeApproximately(2.0 / 3.0, 1e-9);
    }

    [Fact]
    public async Task QueryAccuracy_ShouldExcludeBelowThreshold()
    {
        var store = new InMemoryAiSuggestionStore();
        var r1 = MakeRecord(confidence: 0.4, accepted: true);
        var r2 = MakeRecord(confidence: 0.6, accepted: false);
        await store.SaveAsync(r1, CancellationToken.None);
        await store.SaveAsync(r2, CancellationToken.None);

        var (samples, acceptRate) = await store.QueryAccuracyAsync(
            Tenant, SchemaId, schemaVersion: 1, confidenceThreshold: 0.75, CancellationToken.None);

        samples.Should().Be(0);
        acceptRate.Should().Be(0d);
    }

    [Fact]
    public async Task QueryAccuracy_ShouldReturnZero_WhenNoReconciledSuggestions()
    {
        var store = new InMemoryAiSuggestionStore();

        var (samples, acceptRate) = await store.QueryAccuracyAsync(
            Tenant, SchemaId, schemaVersion: 1, confidenceThreshold: 0.5, CancellationToken.None);

        samples.Should().Be(0);
        acceptRate.Should().Be(0d);
    }

    [Fact]
    public async Task QueryAccuracy_ShouldScopeToTenantAndSchema_WhenOtherDataExists()
    {
        var store = new InMemoryAiSuggestionStore();
        var otherSchema = EntityId.New();
        var otherTenant = EntityId.From("other-tenant");

        // Matching tenant + schema — 1 accepted
        var r1 = MakeRecord(confidence: 0.9, accepted: true);
        // Different schema — must not count
        var r2 = new AiSuggestionRecord
        {
            Id = EntityId.New(), TenantId = Tenant, ConversationId = EntityId.New(),
            SchemaId = otherSchema, SchemaVersion = 1, SuggestedLeafNodeId = EntityId.New(),
            SuggestedNodePath = [], SuggestedFieldValues = new Dictionary<string, string>(),
            Confidence = 0.9, ModelId = "m", PromptVersion = "v1",
            SurfacedBand = TypificationBand.Suggest,
            CreatedAt = DateTimeOffset.UtcNow, Accepted = true,
        };
        // Different tenant — must not count
        var r3 = new AiSuggestionRecord
        {
            Id = EntityId.New(), TenantId = otherTenant, ConversationId = EntityId.New(),
            SchemaId = SchemaId, SchemaVersion = 1, SuggestedLeafNodeId = EntityId.New(),
            SuggestedNodePath = [], SuggestedFieldValues = new Dictionary<string, string>(),
            Confidence = 0.9, ModelId = "m", PromptVersion = "v1",
            SurfacedBand = TypificationBand.Suggest,
            CreatedAt = DateTimeOffset.UtcNow, Accepted = true,
        };

        await store.SaveAsync(r1, CancellationToken.None);
        await store.SaveAsync(r2, CancellationToken.None);
        await store.SaveAsync(r3, CancellationToken.None);

        var (samples, acceptRate) = await store.QueryAccuracyAsync(
            Tenant, SchemaId, schemaVersion: 1, confidenceThreshold: 0.5, CancellationToken.None);

        samples.Should().Be(1);
        acceptRate.Should().Be(1d);
    }

    [Fact]
    public async Task QueryAccuracy_ShouldExcludeAutoFillBandSamples_WhenComputingAccuracy()
    {
        var store = new InMemoryAiSuggestionStore();
        // 2 Suggest-band reconciled rows above threshold (both accepted) → counted.
        var s1 = MakeRecord(confidence: 0.9, accepted: true, surfacedBand: TypificationBand.Suggest);
        var s2 = MakeRecord(confidence: 0.88, accepted: true, surfacedBand: TypificationBand.Suggest);
        // 2 AutoFill-band reconciled rows above threshold → EXCLUDED (gate must not measure its own output).
        var a1 = MakeRecord(confidence: 0.95, accepted: true, surfacedBand: TypificationBand.AutoFill);
        var a2 = MakeRecord(confidence: 0.96, accepted: false, surfacedBand: TypificationBand.AutoFill);

        foreach (var r in new[] { s1, s2, a1, a2 })
            await store.SaveAsync(r, CancellationToken.None);

        var (samples, acceptRate) = await store.QueryAccuracyAsync(
            Tenant, SchemaId, schemaVersion: 1, confidenceThreshold: 0.75, CancellationToken.None);

        samples.Should().Be(2, "only the non-AutoFill-band rows count toward calibration");
        acceptRate.Should().Be(1d);
    }

    [Fact]
    public async Task QueryAccuracy_ShouldExcludeOtherSchemaVersions_WhenFiltering()
    {
        var store = new InMemoryAiSuggestionStore();
        // 2 rows at version 1 (the published version queried) → counted.
        var v1a = MakeRecord(schemaVersion: 1, confidence: 0.9, accepted: true);
        var v1b = MakeRecord(schemaVersion: 1, confidence: 0.85, accepted: false);
        // 2 rows at version 2 (a different published version) → EXCLUDED.
        var v2a = MakeRecord(schemaVersion: 2, confidence: 0.92, accepted: true);
        var v2b = MakeRecord(schemaVersion: 2, confidence: 0.91, accepted: true);

        foreach (var r in new[] { v1a, v1b, v2a, v2b })
            await store.SaveAsync(r, CancellationToken.None);

        var (samples, acceptRate) = await store.QueryAccuracyAsync(
            Tenant, SchemaId, schemaVersion: 1, confidenceThreshold: 0.75, CancellationToken.None);

        samples.Should().Be(2, "only version-1 samples count when querying version 1");
        acceptRate.Should().Be(0.5);
    }
}
