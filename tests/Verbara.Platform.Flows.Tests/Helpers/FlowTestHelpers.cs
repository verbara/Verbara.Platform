using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Flows;

namespace Verbara.Platform.Flows.Tests.Helpers;

internal static class FlowTestHelpers
{
    public static TenantId DefaultTenant { get; } = new TenantId("tenant-test");

    public static FlowNode MakeNode(EntityId id, string type, Dictionary<string, string>? config = null, params FlowEdge[] edges) =>
        new()
        {
            NodeId = id,
            Type = type,
            Config = config ?? new Dictionary<string, string>(),
            Edges = edges,
        };

    public static FlowDefinition MakeFlow(EntityId entryId, bool published, params FlowNode[] nodes) =>
        new()
        {
            FlowId = EntityId.New(),
            TenantId = DefaultTenant,
            Name = "Test Flow",
            Version = 1,
            IsPublished = published,
            EntryNodeId = entryId,
            Nodes = nodes,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    public static FlowExecution MakeRunningExecution(EntityId flowId, EntityId conversationId, EntityId currentNodeId, TenantId? tenant = null) =>
        new()
        {
            ExecutionId = EntityId.New(),
            FlowId = flowId,
            FlowVersion = 1,
            TenantId = tenant ?? DefaultTenant,
            ConversationId = conversationId,
            CurrentNodeId = currentNodeId,
            Status = FlowExecutionStatus.WaitingForInput,
            StartedAt = DateTimeOffset.UtcNow,
        };

    public static MessageEnvelope TextMessage(string text) =>
        new([new TextBlock(text)]);
}
