using System.Text.Json;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Flows;
using Verbara.Platform.Flows.Nodes;
using Verbara.Platform.Flows.Tests.Helpers;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;
using FluentAssertions;

namespace Verbara.Platform.Flows.Tests.NodeHandlers;

public sealed class CollectReasonNodeHandlerTests
{
    // Shared 3-level cascade:
    //   CITAS (root, branch)
    //     └─ REPROG (branch)
    //          └─ GINE (leaf)   ← channel-restricted to WhatsApp in the channel test schema
    //     └─ NUEVA (leaf)
    //   FACTURACION (root, leaf)
    private static readonly EntityId CitasId = EntityId.From("node-citas");
    private static readonly EntityId ReprogId = EntityId.From("node-reprog");
    private static readonly EntityId GineId = EntityId.From("node-gine");
    private static readonly EntityId NuevaId = EntityId.From("node-nueva");
    private static readonly EntityId FacturacionId = EntityId.From("node-fact");

    private static readonly EntityId SchemaId = EntityId.From("schema-typ");

    private static TypificationNode Branch(EntityId id, EntityId? parent, string code, string label, int sort) =>
        new()
        {
            NodeId = id,
            ParentNodeId = parent,
            Code = code,
            Label = label,
            SortOrder = sort,
            IsLeaf = false,
        };

    private static TypificationNode Leaf(
        EntityId id, EntityId? parent, string code, string label, int sort,
        IReadOnlyList<ChannelType>? channels = null) =>
        new()
        {
            NodeId = id,
            ParentNodeId = parent,
            Code = code,
            Label = label,
            SortOrder = sort,
            IsLeaf = true,
            ChannelApplicability = channels,
            Leaf = new LeafOutcome { Category = TypificationCategory.Success },
        };

    private static TypificationSchema MakeSchema(IReadOnlyList<TypificationNode> nodes) =>
        new()
        {
            SchemaId = SchemaId,
            TenantId = FlowTestHelpers.DefaultTenant,
            Name = "Typification",
            Version = 1,
            IsPublished = true,
            Nodes = nodes,
            Fields = [],
            DataDips = [],
            AiConfig = new TypificationAiConfig
            {
                EntityFieldMap = new Dictionary<string, string>(),
            },
        };

    private static TypificationSchema StandardSchema() =>
        MakeSchema(
        [
            Branch(CitasId, null, "CITAS", "Citas", 0),
            Branch(ReprogId, CitasId, "REPROG", "Reprogramación", 0),
            Leaf(GineId, ReprogId, "GINE", "Ginecología", 0),
            Leaf(NuevaId, CitasId, "NUEVA", "Nueva cita", 1),
            Leaf(FacturacionId, null, "FACTURACION", "Facturación", 1),
        ]);

    private static CollectReasonNodeHandler MakeHandler(
        TypificationSchema schema, ChannelType channel = ChannelType.WebChat)
    {
        var schemaStore = new FakeSchemaStore(schema);
        var conversationStore = new FakeConversationStore(channel);
        return new CollectReasonNodeHandler(new TemplateRenderer(), schemaStore, conversationStore);
    }

    private static FlowNode MakeNode(EntityId nodeId, IDictionary<string, string>? extra = null)
    {
        var config = new Dictionary<string, string> { ["schema_id"] = SchemaId.Value };
        if (extra is not null)
        {
            foreach (var kv in extra)
                config[kv.Key] = kv.Value;
        }

        return FlowTestHelpers.MakeNode(nodeId, "collect_reason", config);
    }

    private static FlowExecution MakeExecution(EntityId nodeId) =>
        FlowTestHelpers.MakeRunningExecution(EntityId.New(), EntityId.New(), nodeId);

    [Fact]
    public async Task ExecuteAsync_ShouldRenderTopLevelMenu_WhenFirstEntered()
    {
        var nodeId = EntityId.New();
        var handler = MakeHandler(StandardSchema());
        var node = MakeNode(nodeId);
        var execution = MakeExecution(nodeId);

        var result = await handler.ExecuteAsync(node, execution, inboundMessage: null, CancellationToken.None);

        result.WaitForInput.Should().BeTrue();
        result.NextEdgeCondition.Should().BeNull();

        var outbound = execution.Variables[$"__outbound_{execution.StepCount}"];
        outbound.Should().Contain("1. Citas");
        outbound.Should().Contain("2. Facturación");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldAdvanceOneLevel_WhenValidSelection()
    {
        var nodeId = EntityId.New();
        var handler = MakeHandler(StandardSchema());
        var node = MakeNode(nodeId);
        var execution = MakeExecution(nodeId);

        // Select "1" (Citas branch) → menu should descend to Citas' children.
        var result = await handler.ExecuteAsync(
            node, execution, FlowTestHelpers.TextMessage("1"), CancellationToken.None);

        result.WaitForInput.Should().BeTrue();
        result.NextEdgeCondition.Should().BeNull();
        execution.Variables[$"__reason_current_{nodeId}"].Should().Be(CitasId.Value);

        var outbound = execution.Variables[$"__outbound_{execution.StepCount}"];
        outbound.Should().Contain("1. Reprogramación");
        outbound.Should().Contain("2. Nueva cita");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRetry_WhenInvalidSelection()
    {
        var nodeId = EntityId.New();
        var handler = MakeHandler(StandardSchema());
        var node = MakeNode(nodeId, new Dictionary<string, string>
        {
            ["retry_prompt"] = "Please pick a valid number.",
        });
        var execution = MakeExecution(nodeId);

        var result = await handler.ExecuteAsync(
            node, execution, FlowTestHelpers.TextMessage("99"), CancellationToken.None);

        result.WaitForInput.Should().BeTrue();
        result.NextEdgeCondition.Should().BeNull();
        execution.Variables[$"__reason_retries_{nodeId}"].Should().Be("1");

        // The single retry message includes BOTH the configured retry_prompt AND the
        // re-rendered menu — they must not overwrite each other on the shared step key.
        var outbound = execution.Variables[$"__outbound_{execution.StepCount}"];
        outbound.Should().Contain("Please pick a valid number.");
        outbound.Should().Contain("1. Citas");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldIncludeRetryPromptAndMenu_WhenInvalidSelectionWithRetryPromptConfigured()
    {
        var nodeId = EntityId.New();
        var handler = MakeHandler(StandardSchema());
        var node = MakeNode(nodeId, new Dictionary<string, string>
        {
            ["retry_prompt"] = "That option is not valid.",
        });
        var execution = MakeExecution(nodeId);

        var result = await handler.ExecuteAsync(
            node, execution, FlowTestHelpers.TextMessage("0"), CancellationToken.None);

        result.WaitForInput.Should().BeTrue();
        result.NextEdgeCondition.Should().BeNull();

        // ONE outbound message carrying the retry hint followed by the full menu.
        var outbound = execution.Variables[$"__outbound_{execution.StepCount}"];
        outbound.Should().Contain("That option is not valid.");
        outbound.Should().Contain("1. Citas");
        outbound.Should().Contain("2. Facturación");

        // Retry hint precedes the menu in the composed message.
        outbound.IndexOf("That option is not valid.", StringComparison.Ordinal)
            .Should().BeLessThan(outbound.IndexOf("1. Citas", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRenderMenuOnly_WhenInvalidSelectionWithoutRetryPrompt()
    {
        var nodeId = EntityId.New();
        var handler = MakeHandler(StandardSchema());
        var node = MakeNode(nodeId);
        var execution = MakeExecution(nodeId);

        var result = await handler.ExecuteAsync(
            node, execution, FlowTestHelpers.TextMessage("99"), CancellationToken.None);

        result.WaitForInput.Should().BeTrue();
        execution.Variables[$"__reason_retries_{nodeId}"].Should().Be("1");

        var outbound = execution.Variables[$"__outbound_{execution.StepCount}"];
        outbound.Should().StartWith("1. Citas");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldWriteReasonPathCodes_WhenLeafReached()
    {
        var nodeId = EntityId.New();
        var handler = MakeHandler(StandardSchema());
        var node = MakeNode(nodeId);
        var execution = MakeExecution(nodeId);

        // 1 (Citas) → menu of Citas children {Reprogramación, Nueva cita}
        await handler.ExecuteAsync(node, execution, FlowTestHelpers.TextMessage("1"), CancellationToken.None);
        // 1 (Reprogramación) → menu of {Ginecología}
        await handler.ExecuteAsync(node, execution, FlowTestHelpers.TextMessage("1"), CancellationToken.None);
        // 1 (Ginecología, leaf) → capture
        var result = await handler.ExecuteAsync(
            node, execution, FlowTestHelpers.TextMessage("1"), CancellationToken.None);

        result.WaitForInput.Should().BeFalse();
        result.NextEdgeCondition.Should().Be("collected");

        var json = execution.Variables["reasonPath"];
        var codes = JsonSerializer.Deserialize(json, TypificationJsonContext.Default.StringArray);
        codes.Should().Equal("CITAS", "REPROG", "GINE");

        // Cascade state cleared.
        execution.Variables.Should().NotContainKey($"__reason_current_{nodeId}");
        execution.Variables.Should().NotContainKey($"__reason_retries_{nodeId}");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRespectSubtreeRoot_WhenConfigured()
    {
        var nodeId = EntityId.New();
        var handler = MakeHandler(StandardSchema());
        // Start the menu at the children of CITAS (subtree root), not the true roots.
        var node = MakeNode(nodeId, new Dictionary<string, string>
        {
            ["subtree_root_node_id"] = CitasId.Value,
        });
        var execution = MakeExecution(nodeId);

        // First entry: menu = children of CITAS = {Reprogramación, Nueva cita}.
        var first = await handler.ExecuteAsync(node, execution, inboundMessage: null, CancellationToken.None);
        first.WaitForInput.Should().BeTrue();
        var menu = execution.Variables[$"__outbound_{execution.StepCount}"];
        menu.Should().Contain("1. Reprogramación");
        menu.Should().Contain("2. Nueva cita");
        menu.Should().NotContain("Facturación");

        // 1 (Reprogramación branch) → {Ginecología}
        await handler.ExecuteAsync(node, execution, FlowTestHelpers.TextMessage("1"), CancellationToken.None);
        // 1 (Ginecología leaf) → capture; reasonPath is STILL the full root→leaf path.
        var result = await handler.ExecuteAsync(
            node, execution, FlowTestHelpers.TextMessage("1"), CancellationToken.None);

        result.NextEdgeCondition.Should().Be("collected");
        var codes = JsonSerializer.Deserialize(
            execution.Variables["reasonPath"], TypificationJsonContext.Default.StringArray);
        codes.Should().Equal("CITAS", "REPROG", "GINE");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFilterByChannelApplicability_WhenChannelRestricted()
    {
        var nodeId = EntityId.New();
        // GINE leaf is restricted to WhatsApp; on a WebChat conversation it must be hidden.
        var schema = MakeSchema(
        [
            Branch(CitasId, null, "CITAS", "Citas", 0),
            Branch(ReprogId, CitasId, "REPROG", "Reprogramación", 0),
            Leaf(GineId, ReprogId, "GINE", "Ginecología", 0, channels: [ChannelType.WhatsApp]),
            Leaf(NuevaId, ReprogId, "NUEVA", "Nueva cita", 1),
        ]);

        var handler = MakeHandler(schema, channel: ChannelType.WebChat);
        var node = MakeNode(nodeId);
        var execution = MakeExecution(nodeId);

        // 1 (Citas) → {Reprogramación}
        await handler.ExecuteAsync(node, execution, FlowTestHelpers.TextMessage("1"), CancellationToken.None);
        // 1 (Reprogramación) → children = {Ginecología(WhatsApp-only, filtered out), Nueva cita}
        await handler.ExecuteAsync(node, execution, FlowTestHelpers.TextMessage("1"), CancellationToken.None);

        var menu = execution.Variables[$"__outbound_{execution.StepCount}"];
        menu.Should().NotContain("Ginecología");   // filtered: WhatsApp-only on a WebChat conversation
        menu.Should().Contain("1. Nueva cita");    // the only applicable child, renumbered to 1
    }

    private sealed class FakeSchemaStore : ITypificationSchemaStore
    {
        private readonly TypificationSchema _schema;

        public FakeSchemaStore(TypificationSchema schema) => _schema = schema;

        public Task<TypificationSchema?> GetLatestPublishedAsync(
            TenantId tenantId, EntityId schemaId, CancellationToken ct) =>
            Task.FromResult<TypificationSchema?>(
                schemaId == _schema.SchemaId && _schema.IsPublished ? _schema : null);

        public Task<TypificationSchema?> GetByIdAsync(
            TenantId tenantId, EntityId schemaId, int? version, CancellationToken ct) =>
            Task.FromResult<TypificationSchema?>(schemaId == _schema.SchemaId ? _schema : null);

        public Task<IReadOnlyList<TypificationSchema>> ListAsync(TenantId tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TypificationSchema>>([_schema]);

        public Task SaveAsync(TypificationSchema schema, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteAsync(TenantId tenantId, EntityId schemaId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeConversationStore : IConversationStore
    {
        private readonly ChannelType _channel;

        public FakeConversationStore(ChannelType channel) => _channel = channel;

        public Task<Conversation?> GetByIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct) =>
            Task.FromResult<Conversation?>(new Conversation
            {
                ConversationId = conversationId,
                TenantId = tenantId,
                ContactId = EntityId.New(),
                Channel = _channel,
                State = ConversationState.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        public Task<PagedResult<Conversation>> ListAsync(TenantId tenantId, ConversationQuery query, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SaveAsync(Conversation conversation, CancellationToken ct) => Task.CompletedTask;

        public Task<Conversation?> FindActiveByContactAsync(
            TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct) =>
            Task.FromResult<Conversation?>(null);

        public Task<Conversation?> FindByVoiceLinkedIdAsync(
            TenantId tenantId, string voiceLinkedId, CancellationToken ct) =>
            Task.FromResult<Conversation?>(null);

        public Task<Conversation?> FindByVoiceLinkedIdAcrossTenantsAsync(
            string voiceLinkedId, CancellationToken ct) =>
            Task.FromResult<Conversation?>(null);

        public Task<Conversation?> FindByIdAcrossTenantsAsync(
            EntityId conversationId, CancellationToken ct) =>
            Task.FromResult<Conversation?>(null);

        public Task<IReadOnlyList<Conversation>> ListByContactAsync(
            TenantId tenantId, EntityId contactId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Conversation>>([]);

        public Task<int> DeleteByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct) =>
            Task.FromResult(0);

        public Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<Conversation>> ListQueuedAsync(TenantId tenantId, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Conversation>>([]);

        public Task<IReadOnlyList<Conversation>> ListByStateAsync(
            TenantId tenantId, ConversationState state, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Conversation>>([]);

        public Task<bool> TryConditionalCloseWrapUpAsync(
            TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<int> CountActiveWorkAsync(TenantId tenantId, EntityId agentId, CancellationToken ct) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<Conversation>> ListFailoverWorkByOwnerAsync(
            TenantId tenantId, EntityId agentId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Conversation>>([]);

        public Task<IReadOnlyList<Conversation>> ListPendingCallbackEvalAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Conversation>>([]);

        public Task<IReadOnlyList<Conversation>> ListCallbackStuckAsync(TenantId tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Conversation>>([]);
    }
}
