using Asterisk.Platform.Core;

namespace Asterisk.Platform.Flows;

/// <summary>
/// Validates a <see cref="FlowDefinition"/> DAG before publishing or executing it.
/// </summary>
public static class FlowValidator
{
    private const int MaxNodes = 100;

    /// <summary>
    /// Validates the flow and returns a result that lists all discovered errors.
    /// </summary>
    public static FlowValidationResult Validate(FlowDefinition flow)
    {
        var errors = new List<string>();

        if (flow.Nodes.Count == 0)
        {
            errors.Add("Flow must contain at least one node.");
            return new FlowValidationResult(false, errors);
        }

        if (flow.Nodes.Count > MaxNodes)
            errors.Add($"Flow exceeds maximum node limit of {MaxNodes} (found {flow.Nodes.Count}).");

        // Build a lookup by NodeId.
        var nodeMap = new Dictionary<string, FlowNode>(flow.Nodes.Count);
        foreach (var node in flow.Nodes)
        {
            var id = node.NodeId.Value;
            if (!nodeMap.TryAdd(id, node))
                errors.Add($"Duplicate node ID: {id}.");
        }

        // Entry node must exist.
        if (!nodeMap.ContainsKey(flow.EntryNodeId.Value))
            errors.Add($"Entry node '{flow.EntryNodeId}' does not exist in the node list.");

        // All edge targets must exist.
        foreach (var node in flow.Nodes)
        {
            foreach (var edge in node.Edges)
            {
                if (!nodeMap.ContainsKey(edge.TargetNodeId.Value))
                    errors.Add($"Node '{node.NodeId}' has edge pointing to unknown target '{edge.TargetNodeId}'.");
            }
        }

        // Cycle detection via topological sort (Kahn's algorithm).
        if (errors.Count == 0 && flow.Nodes.Count > 1)
        {
            if (HasCycle(flow.Nodes))
                errors.Add("Flow contains a cycle — DAG must be acyclic.");
        }

        return new FlowValidationResult(errors.Count == 0, errors);
    }

    private static bool HasCycle(IReadOnlyList<FlowNode> nodes)
    {
        // Build adjacency and in-degree maps.
        var inDegree = new Dictionary<string, int>(nodes.Count);
        var adj = new Dictionary<string, List<string>>(nodes.Count);

        foreach (var node in nodes)
        {
            var id = node.NodeId.Value;
            inDegree.TryAdd(id, 0);
            if (!adj.ContainsKey(id))
                adj[id] = new List<string>();

            foreach (var edge in node.Edges)
            {
                var target = edge.TargetNodeId.Value;
                adj[id].Add(target);
                inDegree.TryAdd(target, 0);
                inDegree[target]++;
            }
        }

        var queue = new Queue<string>();
        foreach (var kv in inDegree)
        {
            if (kv.Value == 0)
                queue.Enqueue(kv.Key);
        }

        var visited = 0;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            visited++;
            if (!adj.TryGetValue(current, out var neighbours))
                continue;

            foreach (var neighbour in neighbours)
            {
                inDegree[neighbour]--;
                if (inDegree[neighbour] == 0)
                    queue.Enqueue(neighbour);
            }
        }

        return visited < nodes.Count;
    }
}

/// <summary>
/// The result of a <see cref="FlowValidator.Validate"/> call.
/// </summary>
/// <param name="IsValid"><c>true</c> if no errors were found.</param>
/// <param name="Errors">List of validation error messages (empty when valid).</param>
public sealed record FlowValidationResult(bool IsValid, IReadOnlyList<string> Errors);
