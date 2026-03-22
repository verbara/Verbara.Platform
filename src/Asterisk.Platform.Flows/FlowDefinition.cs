using Asterisk.Platform.Core;

namespace Asterisk.Platform.Flows;

/// <summary>
/// The DAG blueprint that describes a flow — its nodes, edges, and entry point.
/// </summary>
public sealed class FlowDefinition : ITenantScoped
{
    public required EntityId FlowId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Name { get; set; }
    public required int Version { get; init; }
    public bool IsPublished { get; set; }
    public required EntityId EntryNodeId { get; init; }
    public required IReadOnlyList<FlowNode> Nodes { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
