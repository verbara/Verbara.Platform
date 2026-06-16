using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Resolution;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Typification.Tests;

public sealed class DefaultTypificationResolverTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly EntityId QueueId = EntityId.From("queue-7");
    private const string CampaignId = "campaign-3";

    // ---------- precedence ----------

    [Fact]
    public async Task ResolveForConversation_ShouldPickQueue_WhenQueueAndChannelBindingsBothMatch()
    {
        var bindings = new FakeBindingStore();
        var schemas = new FakeSchemaStore();

        var queueSchema = PublishedSchema("schema-queue");
        var channelSchema = PublishedSchema("schema-channel");
        schemas.Add(queueSchema);
        schemas.Add(channelSchema);

        bindings.Add(Binding("b-queue", BindingScope.Queue, QueueId.Value, queueSchema.SchemaId));
        bindings.Add(Binding("b-channel", BindingScope.Channel, nameof(ChannelType.Voice), channelSchema.SchemaId));

        var sut = new DefaultTypificationResolver(bindings, schemas);

        var result = await sut.ResolveForConversationAsync(
            Conversation(ChannelType.Voice, queue: QueueId), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Schema.SchemaId.Should().Be(queueSchema.SchemaId);
        result.SubtreeRoot.Should().BeNull();
    }

    [Fact]
    public async Task ResolveForConversation_ShouldFallBackToTenantDefault_WhenNoSpecificBinding()
    {
        var bindings = new FakeBindingStore();
        var schemas = new FakeSchemaStore();

        var tenantSchema = PublishedSchema("schema-tenant");
        schemas.Add(tenantSchema);
        bindings.Add(Binding("b-tenant", BindingScope.Tenant, scopeRef: null, tenantSchema.SchemaId));

        var sut = new DefaultTypificationResolver(bindings, schemas);

        // No queue owner, no campaign metadata, no channel/direction binding → tenant default.
        var result = await sut.ResolveForConversationAsync(
            Conversation(ChannelType.Email), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Schema.SchemaId.Should().Be(tenantSchema.SchemaId);
    }

    // ---------- direction inference ----------

    [Fact]
    public async Task ResolveForConversation_ShouldInferOutbound_WhenMetadataHasCampaignId()
    {
        var bindings = new FakeBindingStore();
        var schemas = new FakeSchemaStore();

        var outboundSchema = PublishedSchema("schema-outbound");
        schemas.Add(outboundSchema);
        bindings.Add(Binding("b-outbound", BindingScope.Direction, "outbound", outboundSchema.SchemaId));

        var sut = new DefaultTypificationResolver(bindings, schemas);

        // campaignId present → direction inferred outbound. There is no Campaign-scope
        // binding, so resolution falls through to the Direction scope.
        var conversation = Conversation(ChannelType.Voice);
        conversation.SetMetadata("campaignId", CampaignId);

        var result = await sut.ResolveForConversationAsync(conversation, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Schema.SchemaId.Should().Be(outboundSchema.SchemaId);
    }

    [Fact]
    public async Task ResolveForConversation_ShouldInferInbound_WhenNoCampaignMetadata()
    {
        var bindings = new FakeBindingStore();
        var schemas = new FakeSchemaStore();

        var inboundSchema = PublishedSchema("schema-inbound");
        var outboundSchema = PublishedSchema("schema-outbound");
        schemas.Add(inboundSchema);
        schemas.Add(outboundSchema);
        bindings.Add(Binding("b-inbound", BindingScope.Direction, "inbound", inboundSchema.SchemaId));
        bindings.Add(Binding("b-outbound", BindingScope.Direction, "outbound", outboundSchema.SchemaId));

        var sut = new DefaultTypificationResolver(bindings, schemas);

        // No campaignId / callAttemptId metadata → direction inferred inbound.
        var result = await sut.ResolveForConversationAsync(
            Conversation(ChannelType.Voice), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Schema.SchemaId.Should().Be(inboundSchema.SchemaId);
    }

    // ---------- tie-break ----------

    [Fact]
    public async Task ResolveForConversation_ShouldBreakTieByPriority_WhenSameScope()
    {
        var bindings = new FakeBindingStore();
        var schemas = new FakeSchemaStore();

        var lowPrioritySchema = PublishedSchema("schema-low");
        var highPrioritySchema = PublishedSchema("schema-high");
        schemas.Add(lowPrioritySchema);
        schemas.Add(highPrioritySchema);

        bindings.Add(Binding("b-low", BindingScope.Queue, QueueId.Value, lowPrioritySchema.SchemaId, priority: 1));
        bindings.Add(Binding("b-high", BindingScope.Queue, QueueId.Value, highPrioritySchema.SchemaId, priority: 9));

        var sut = new DefaultTypificationResolver(bindings, schemas);

        var result = await sut.ResolveForConversationAsync(
            Conversation(ChannelType.Voice, queue: QueueId), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Schema.SchemaId.Should().Be(highPrioritySchema.SchemaId);
    }

    [Fact]
    public async Task ResolveForConversation_ShouldPreferMostRecentBinding_WhenSameScopeAndPriority()
    {
        var bindings = new FakeBindingStore();
        var schemas = new FakeSchemaStore();

        var olderSchema = PublishedSchema("schema-older");
        var newerSchema = PublishedSchema("schema-newer");
        schemas.Add(olderSchema);
        schemas.Add(newerSchema);

        var older = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        // Same scope + same priority. The newer binding intentionally has an id that
        // LOSES the ordinal tiebreak ("b-z" > "b-a"), so a win can only come from the
        // CreatedAt DESC tiebreak — proving most-recent-wins, not id-wins.
        bindings.Add(Binding("b-a", BindingScope.Queue, QueueId.Value, olderSchema.SchemaId, priority: 5, createdAt: older));
        bindings.Add(Binding("b-z", BindingScope.Queue, QueueId.Value, newerSchema.SchemaId, priority: 5, createdAt: newer));

        var sut = new DefaultTypificationResolver(bindings, schemas);

        var result = await sut.ResolveForConversationAsync(
            Conversation(ChannelType.Voice, queue: QueueId), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Schema.SchemaId.Should().Be(newerSchema.SchemaId);
    }

    // ---------- no match ----------

    [Fact]
    public async Task ResolveForConversation_ShouldReturnNull_WhenNoBindingMatches()
    {
        var bindings = new FakeBindingStore();
        var schemas = new FakeSchemaStore();

        var sut = new DefaultTypificationResolver(bindings, schemas);

        var result = await sut.ResolveForConversationAsync(
            Conversation(ChannelType.Voice, queue: QueueId), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveForConversation_ShouldSkipBinding_WhenSchemaHasNoPublishedVersion()
    {
        var bindings = new FakeBindingStore();
        var schemas = new FakeSchemaStore();

        // Queue binding points at a schema with NO published version → skipped, falls
        // through to the tenant default.
        var unpublishedId = EntityId.From("schema-unpublished");
        var tenantSchema = PublishedSchema("schema-tenant");
        schemas.Add(tenantSchema);

        bindings.Add(Binding("b-queue", BindingScope.Queue, QueueId.Value, unpublishedId));
        bindings.Add(Binding("b-tenant", BindingScope.Tenant, scopeRef: null, tenantSchema.SchemaId));

        var sut = new DefaultTypificationResolver(bindings, schemas);

        var result = await sut.ResolveForConversationAsync(
            Conversation(ChannelType.Voice, queue: QueueId), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Schema.SchemaId.Should().Be(tenantSchema.SchemaId);
    }

    // ---------- E1: effective AI config (binding override ?? schema) ----------

    [Fact]
    public async Task Resolve_ShouldUseBindingAiConfigOverride_WhenPresent()
    {
        var bindings = new FakeBindingStore();
        var schemas = new FakeSchemaStore();

        // Schema default is SuggestOnly; the binding override pilots AutoFill on this queue only.
        var schema = PublishedSchema("schema-queue", mode: AiMode.SuggestOnly);
        schemas.Add(schema);

        var overrideConfig = new TypificationAiConfig
        {
            Enabled = true,
            Mode = AiMode.AutoFill,
            EntityFieldMap = new Dictionary<string, string>(),
        };
        bindings.Add(Binding("b-queue", BindingScope.Queue, QueueId.Value, schema.SchemaId, aiConfigOverride: overrideConfig));

        var sut = new DefaultTypificationResolver(bindings, schemas);

        var result = await sut.ResolveForConversationAsync(
            Conversation(ChannelType.Voice, queue: QueueId), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Schema.SchemaId.Should().Be(schema.SchemaId);
        // The schema's own default is unchanged; the EFFECTIVE config is the override.
        result.Schema.AiConfig.Mode.Should().Be(AiMode.SuggestOnly);
        result.EffectiveAiConfig.Mode.Should().Be(AiMode.AutoFill);
    }

    [Fact]
    public async Task Resolve_ShouldFallBackToSchemaAiConfig_WhenNoOverride()
    {
        var bindings = new FakeBindingStore();
        var schemas = new FakeSchemaStore();

        var schema = PublishedSchema("schema-queue", mode: AiMode.SuggestOnly);
        schemas.Add(schema);

        // No AiConfigOverride on the binding → the effective config is the schema's own.
        bindings.Add(Binding("b-queue", BindingScope.Queue, QueueId.Value, schema.SchemaId));

        var sut = new DefaultTypificationResolver(bindings, schemas);

        var result = await sut.ResolveForConversationAsync(
            Conversation(ChannelType.Voice, queue: QueueId), CancellationToken.None);

        result.Should().NotBeNull();
        result!.EffectiveAiConfig.Should().BeSameAs(schema.AiConfig);
        result.EffectiveAiConfig.Mode.Should().Be(AiMode.SuggestOnly);
    }

    // ---------- helpers ----------

    private static Conversation Conversation(ChannelType channel, EntityId? queue = null) =>
        new()
        {
            ConversationId = EntityId.From("conv-1"),
            TenantId = Tenant,
            ContactId = EntityId.From("contact-1"),
            Channel = channel,
            State = ConversationState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            Owner = queue is { } q ? ConversationOwner.ForQueue(q) : null,
        };

    private static SchemaBinding Binding(
        string id,
        BindingScope scope,
        string? scopeRef,
        EntityId schemaId,
        int priority = 0,
        EntityId? subtreeRoot = null,
        DateTimeOffset? createdAt = null,
        TypificationAiConfig? aiConfigOverride = null) =>
        new()
        {
            BindingId = EntityId.From(id),
            TenantId = Tenant,
            Scope = scope,
            ScopeRef = scopeRef,
            SchemaId = schemaId,
            SubTreeRootNodeId = subtreeRoot,
            Priority = priority,
            AiConfigOverride = aiConfigOverride,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };

    private static TypificationSchema PublishedSchema(string id, AiMode mode = AiMode.Off) =>
        new()
        {
            SchemaId = EntityId.From(id),
            TenantId = Tenant,
            Name = id,
            Version = 1,
            IsPublished = true,
            Nodes = [],
            Fields = [],
            DataDips = [],
            AiConfig = new TypificationAiConfig
            {
                Enabled = mode != AiMode.Off,
                Mode = mode,
                EntityFieldMap = new Dictionary<string, string>(),
            },
        };

    private sealed class FakeBindingStore : ISchemaBindingStore
    {
        private readonly List<SchemaBinding> _bindings = [];

        public void Add(SchemaBinding binding) => _bindings.Add(binding);

        public Task<IReadOnlyList<SchemaBinding>> ListByScopeAsync(
            TenantId tenantId, BindingScope scope, string? scopeRef, CancellationToken ct)
        {
            IReadOnlyList<SchemaBinding> matches = _bindings
                .Where(b => b.TenantId == tenantId && b.Scope == scope && b.ScopeRef == scopeRef)
                .ToList();
            return Task.FromResult(matches);
        }

        public Task<SchemaBinding?> GetByIdAsync(TenantId tenantId, EntityId bindingId, CancellationToken ct) =>
            Task.FromResult(_bindings.FirstOrDefault(b => b.TenantId == tenantId && b.BindingId == bindingId));

        public Task<IReadOnlyList<SchemaBinding>> ListAsync(TenantId tenantId, CancellationToken ct)
        {
            IReadOnlyList<SchemaBinding> all = _bindings.Where(b => b.TenantId == tenantId).ToList();
            return Task.FromResult(all);
        }

        public Task SaveAsync(SchemaBinding binding, CancellationToken ct)
        {
            _bindings.Add(binding);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(TenantId tenantId, EntityId bindingId, CancellationToken ct)
        {
            _bindings.RemoveAll(b => b.TenantId == tenantId && b.BindingId == bindingId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSchemaStore : ITypificationSchemaStore
    {
        private readonly List<TypificationSchema> _schemas = [];

        public void Add(TypificationSchema schema) => _schemas.Add(schema);

        public Task<TypificationSchema?> GetLatestPublishedAsync(TenantId tenantId, EntityId schemaId, CancellationToken ct)
        {
            var latest = _schemas
                .Where(s => s.TenantId == tenantId && s.SchemaId == schemaId && s.IsPublished)
                .OrderByDescending(s => s.Version)
                .FirstOrDefault();
            return Task.FromResult(latest);
        }

        public Task<TypificationSchema?> GetByIdAsync(TenantId tenantId, EntityId schemaId, int? version, CancellationToken ct)
        {
            var match = _schemas
                .Where(s => s.TenantId == tenantId && s.SchemaId == schemaId && (version is null || s.Version == version))
                .OrderByDescending(s => s.Version)
                .FirstOrDefault();
            return Task.FromResult(match);
        }

        public Task<IReadOnlyList<TypificationSchema>> ListAsync(TenantId tenantId, CancellationToken ct)
        {
            IReadOnlyList<TypificationSchema> all = _schemas.Where(s => s.TenantId == tenantId).ToList();
            return Task.FromResult(all);
        }

        public Task SaveAsync(TypificationSchema schema, CancellationToken ct)
        {
            _schemas.Add(schema);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(TenantId tenantId, EntityId schemaId, CancellationToken ct)
        {
            _schemas.RemoveAll(s => s.TenantId == tenantId && s.SchemaId == schemaId);
            return Task.CompletedTask;
        }
    }
}
