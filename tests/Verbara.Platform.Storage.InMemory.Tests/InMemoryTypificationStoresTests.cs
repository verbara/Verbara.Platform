using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Typification;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryTypificationStoresTests
{
    private static readonly TenantId Tenant = new("tenant-1");

    private static TypificationSchema MakeSchema(EntityId schemaId, int version, bool published) => new()
    {
        SchemaId = schemaId,
        TenantId = Tenant,
        Name = "Schema",
        Version = version,
        IsPublished = published,
        MaxDepth = 5,
        Nodes = [],
        Fields = [],
        DataDips = [],
        AiConfig = new TypificationAiConfig
        {
            Enabled = false,
            Mode = AiMode.Off,
            SentimentGating = false,
            EntityFieldMap = new Dictionary<string, string>(),
        },
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task GetByIdAsync_ShouldReturnHighestVersion_WhenVersionNull()
    {
        var store = new InMemoryTypificationSchemaStore();
        var schemaId = EntityId.New();
        await store.SaveAsync(MakeSchema(schemaId, 1, published: true), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaId, 3, published: false), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaId, 2, published: true), CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant, schemaId, version: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Version.Should().Be(3);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnExactVersion_WhenVersionSpecified()
    {
        var store = new InMemoryTypificationSchemaStore();
        var schemaId = EntityId.New();
        await store.SaveAsync(MakeSchema(schemaId, 1, published: true), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaId, 2, published: true), CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant, schemaId, version: 1, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Version.Should().Be(1);
    }

    [Fact]
    public async Task GetLatestPublishedAsync_ShouldIgnoreNewerDraft_WhenLatestUnpublished()
    {
        var store = new InMemoryTypificationSchemaStore();
        var schemaId = EntityId.New();
        await store.SaveAsync(MakeSchema(schemaId, 1, published: true), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaId, 2, published: true), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaId, 3, published: false), CancellationToken.None);

        var result = await store.GetLatestPublishedAsync(Tenant, schemaId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Version.Should().Be(2);
    }

    [Fact]
    public async Task ListAsync_ShouldReturnLatestVersionPerSchema()
    {
        var store = new InMemoryTypificationSchemaStore();
        var schemaA = EntityId.New();
        var schemaB = EntityId.New();
        await store.SaveAsync(MakeSchema(schemaA, 1, published: true), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaA, 2, published: false), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaB, 1, published: true), CancellationToken.None);

        var result = await store.ListAsync(Tenant, CancellationToken.None);

        result.Should().HaveCount(2);
        result.Single(s => s.SchemaId == schemaA).Version.Should().Be(2);
        result.Single(s => s.SchemaId == schemaB).Version.Should().Be(1);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveAllVersions()
    {
        var store = new InMemoryTypificationSchemaStore();
        var schemaId = EntityId.New();
        await store.SaveAsync(MakeSchema(schemaId, 1, published: true), CancellationToken.None);
        await store.SaveAsync(MakeSchema(schemaId, 2, published: true), CancellationToken.None);

        await store.DeleteAsync(Tenant, schemaId, CancellationToken.None);

        (await store.GetByIdAsync(Tenant, schemaId, version: null, CancellationToken.None)).Should().BeNull();
        (await store.GetByIdAsync(Tenant, schemaId, version: 1, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ListByScopeAsync_ShouldFilterByScopeAndRef()
    {
        var store = new InMemorySchemaBindingStore();
        var schemaId = EntityId.New();

        var queue1 = new SchemaBinding
        {
            BindingId = EntityId.New(), TenantId = Tenant, Scope = BindingScope.Queue,
            ScopeRef = "q1", SchemaId = schemaId, Priority = 0, CreatedAt = DateTimeOffset.UtcNow,
        };
        var tenantDefault = new SchemaBinding
        {
            BindingId = EntityId.New(), TenantId = Tenant, Scope = BindingScope.Tenant,
            ScopeRef = null, SchemaId = schemaId, Priority = 0, CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(queue1, CancellationToken.None);
        await store.SaveAsync(tenantDefault, CancellationToken.None);

        var byQueue = await store.ListByScopeAsync(Tenant, BindingScope.Queue, "q1", CancellationToken.None);
        byQueue.Should().ContainSingle().Which.BindingId.Should().Be(queue1.BindingId);

        var byTenant = await store.ListByScopeAsync(Tenant, BindingScope.Tenant, null, CancellationToken.None);
        byTenant.Should().ContainSingle().Which.BindingId.Should().Be(tenantDefault.BindingId);

        // null ref must not match a non-null scope_ref binding.
        var queueNoRef = await store.ListByScopeAsync(Tenant, BindingScope.Queue, null, CancellationToken.None);
        queueNoRef.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_ShouldPreserveCreatedAt_WhenUpsertingBinding()
    {
        var store = new InMemorySchemaBindingStore();
        var bindingId = EntityId.New();
        var original = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var attemptedOverwrite = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var binding = new SchemaBinding
        {
            BindingId = bindingId, TenantId = Tenant, Scope = BindingScope.Queue,
            ScopeRef = "q1", SchemaId = EntityId.New(), Priority = 0, CreatedAt = original,
        };
        await store.SaveAsync(binding, CancellationToken.None);

        // Update with a DIFFERENT incoming CreatedAt — it must be ignored on upsert.
        await store.SaveAsync(binding with { Priority = 9, CreatedAt = attemptedOverwrite }, CancellationToken.None);

        var loaded = await store.GetByIdAsync(Tenant, bindingId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Priority.Should().Be(9);
        loaded.CreatedAt.Should().Be(original);
    }

    [Fact]
    public async Task SaveAsync_ShouldRoundTripAiConfigOverride_WhenSetOnBinding()
    {
        var store = new InMemorySchemaBindingStore();
        var bindingId = EntityId.New();

        var overrideConfig = new TypificationAiConfig
        {
            Enabled = true,
            Mode = AiMode.AutoFill,
            EntityFieldMap = new Dictionary<string, string>(),
        };
        var binding = new SchemaBinding
        {
            BindingId = bindingId, TenantId = Tenant, Scope = BindingScope.Queue,
            ScopeRef = "q1", SchemaId = EntityId.New(), Priority = 0,
            AiConfigOverride = overrideConfig, CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(binding, CancellationToken.None);

        var loaded = await store.GetByIdAsync(Tenant, bindingId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.AiConfigOverride.Should().NotBeNull();
        loaded.AiConfigOverride!.Mode.Should().Be(AiMode.AutoFill);
    }

    [Fact]
    public async Task SaveAsync_ShouldLeaveAiConfigOverrideNull_WhenNotSetOnBinding()
    {
        var store = new InMemorySchemaBindingStore();
        var bindingId = EntityId.New();

        var binding = new SchemaBinding
        {
            BindingId = bindingId, TenantId = Tenant, Scope = BindingScope.Queue,
            ScopeRef = "q1", SchemaId = EntityId.New(), Priority = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(binding, CancellationToken.None);

        var loaded = await store.GetByIdAsync(Tenant, bindingId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.AiConfigOverride.Should().BeNull();
    }

    private static ReasonHint MakeHint(ReasonHintScope scope, string scopeRef, string reasonPath, int priority = 0) => new()
    {
        HintId = EntityId.New(),
        TenantId = Tenant,
        Scope = scope,
        ScopeRef = scopeRef,
        ReasonPath = reasonPath,
        Priority = priority,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task SaveAsync_ShouldUpsert_WhenHintExists()
    {
        var store = new InMemoryReasonHintStore();
        var hintId = EntityId.New();

        var original = new ReasonHint
        {
            HintId = hintId, TenantId = Tenant, Scope = ReasonHintScope.Queue,
            ScopeRef = "q1", ReasonPath = "[\"A\"]", Priority = 0, CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(original, CancellationToken.None);

        var updated = original with { ReasonPath = "[\"B\"]", Priority = 7 };
        await store.SaveAsync(updated, CancellationToken.None);

        var all = await store.ListAsync(Tenant, CancellationToken.None);
        all.Should().ContainSingle();

        var loaded = await store.GetByIdAsync(Tenant, hintId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.ReasonPath.Should().Be("[\"B\"]");
        loaded.Priority.Should().Be(7);
    }

    [Fact]
    public async Task SaveAsync_ShouldPreserveCreatedAt_WhenUpsertingHint()
    {
        var store = new InMemoryReasonHintStore();
        var hintId = EntityId.New();
        var original = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var attemptedOverwrite = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var hint = new ReasonHint
        {
            HintId = hintId, TenantId = Tenant, Scope = ReasonHintScope.Queue,
            ScopeRef = "q1", ReasonPath = "[\"A\"]", Priority = 0, CreatedAt = original,
        };
        await store.SaveAsync(hint, CancellationToken.None);

        // Update with a DIFFERENT incoming CreatedAt — it must be ignored on upsert.
        await store.SaveAsync(hint with { Priority = 9, CreatedAt = attemptedOverwrite }, CancellationToken.None);

        var loaded = await store.GetByIdAsync(Tenant, hintId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Priority.Should().Be(9);
        loaded.CreatedAt.Should().Be(original);
    }

    [Fact]
    public async Task ListByScopeAsync_ShouldReturnOnlyMatchingScope_WhenFiltered()
    {
        var store = new InMemoryReasonHintStore();

        var did = MakeHint(ReasonHintScope.Did, "+15551234567", "[\"CITAS\"]");
        var queueA = MakeHint(ReasonHintScope.Queue, "q1", "[\"REPROG\"]");
        var queueB = MakeHint(ReasonHintScope.Queue, "q2", "[\"GINE\"]");
        await store.SaveAsync(did, CancellationToken.None);
        await store.SaveAsync(queueA, CancellationToken.None);
        await store.SaveAsync(queueB, CancellationToken.None);

        var byQueueA = await store.ListByScopeAsync(Tenant, ReasonHintScope.Queue, "q1", CancellationToken.None);
        byQueueA.Should().ContainSingle().Which.HintId.Should().Be(queueA.HintId);

        var byDid = await store.ListByScopeAsync(Tenant, ReasonHintScope.Did, "+15551234567", CancellationToken.None);
        byDid.Should().ContainSingle().Which.HintId.Should().Be(did.HintId);

        // Same scope, non-matching ref must not match.
        var byQueueMissing = await store.ListByScopeAsync(Tenant, ReasonHintScope.Queue, "q9", CancellationToken.None);
        byQueueMissing.Should().BeEmpty();

        // Matching ref but wrong scope must not match.
        var didAsQueue = await store.ListByScopeAsync(Tenant, ReasonHintScope.Did, "q1", CancellationToken.None);
        didAsQueue.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveHint_WhenPresent()
    {
        var store = new InMemoryReasonHintStore();
        var hint = MakeHint(ReasonHintScope.Channel, "WebChat", "[\"SUPPORT\"]");
        await store.SaveAsync(hint, CancellationToken.None);

        await store.DeleteAsync(Tenant, hint.HintId, CancellationToken.None);

        (await store.GetByIdAsync(Tenant, hint.HintId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SubmissionStore_ShouldUpsertByConversation()
    {
        var store = new InMemoryTypificationSubmissionStore();
        var conversationId = EntityId.New();

        TypificationSubmission MakeSubmission(string note) => new()
        {
            TenantId = Tenant,
            ConversationId = conversationId,
            AgentId = EntityId.New(),
            SchemaId = EntityId.New(),
            SchemaVersion = 1,
            SelectedNodePath = [EntityId.New()],
            LeafNodeId = EntityId.New(),
            FieldValues = new Dictionary<string, string>(),
            Notes = note,
            Source = SubmissionSource.Manual,
            Duration = TimeSpan.Zero,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        await store.SaveAsync(MakeSubmission("first"), CancellationToken.None);
        await store.SaveAsync(MakeSubmission("second"), CancellationToken.None);

        var loaded = await store.GetByConversationIdAsync(Tenant, conversationId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Notes.Should().Be("second");
    }

    [Fact]
    public async Task SubmissionStore_ShouldRoundTripAutonomousFields_WhenAutoAiStamped()
    {
        var store = new InMemoryTypificationSubmissionStore();
        var conversationId = EntityId.New();
        var correctedAt = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var correctsConv = EntityId.New();

        var submission = new TypificationSubmission
        {
            TenantId = Tenant,
            ConversationId = conversationId,
            AgentId = EntityId.New(),
            SchemaId = EntityId.New(),
            SchemaVersion = 1,
            SelectedNodePath = [EntityId.New()],
            LeafNodeId = EntityId.New(),
            FieldValues = new Dictionary<string, string>(),
            Source = SubmissionSource.AutoAi,
            AutonomousActorId = "verbara:ai:autonomous-worker",
            CorrectionState = CorrectionState.Corrected,
            CorrectedAt = correctedAt,
            CorrectsConversationId = correctsConv,
            Duration = TimeSpan.Zero,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        await store.SaveAsync(submission, CancellationToken.None);

        var loaded = await store.GetByConversationIdAsync(Tenant, conversationId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Source.Should().Be(SubmissionSource.AutoAi);
        loaded.AutonomousActorId.Should().Be("verbara:ai:autonomous-worker");
        loaded.CorrectionState.Should().Be(CorrectionState.Corrected);
        loaded.CorrectedAt.Should().Be(correctedAt);
        loaded.CorrectsConversationId.Should().Be(correctsConv);
    }

    [Fact]
    public async Task SubmissionStore_ShouldDefaultCorrectionStateToNone_WhenNotSet()
    {
        var store = new InMemoryTypificationSubmissionStore();
        var conversationId = EntityId.New();

        var submission = new TypificationSubmission
        {
            TenantId = Tenant,
            ConversationId = conversationId,
            AgentId = EntityId.New(),
            SchemaId = EntityId.New(),
            SchemaVersion = 1,
            SelectedNodePath = [EntityId.New()],
            LeafNodeId = EntityId.New(),
            FieldValues = new Dictionary<string, string>(),
            Source = SubmissionSource.Manual,
            Duration = TimeSpan.Zero,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        await store.SaveAsync(submission, CancellationToken.None);

        var loaded = await store.GetByConversationIdAsync(Tenant, conversationId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.CorrectionState.Should().Be(CorrectionState.None);
        loaded.AutonomousActorId.Should().BeNull();
        loaded.CorrectedAt.Should().BeNull();
        loaded.CorrectsConversationId.Should().BeNull();
    }
}
