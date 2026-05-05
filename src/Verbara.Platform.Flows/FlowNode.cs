using Verbara.Platform.Core;

namespace Verbara.Platform.Flows;

/// <summary>
/// A single node in the flow DAG with its type, configuration, and outgoing edges.
/// </summary>
public sealed class FlowNode
{
    public required EntityId NodeId { get; init; }
    public required string Type { get; init; }
    public required IReadOnlyDictionary<string, string> Config { get; init; }
    public required IReadOnlyList<FlowEdge> Edges { get; init; }
}

/// <summary>
/// A directed edge from one node to another, activated when <see cref="Condition"/> matches.
/// </summary>
public sealed record FlowEdge(string Condition, EntityId TargetNodeId);
