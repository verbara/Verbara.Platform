using Verbara.Platform.Conversations;

namespace Verbara.Platform.Flows;

/// <summary>
/// Handles execution of a specific node type within a flow.
/// </summary>
public interface IFlowNodeHandler
{
    /// <summary>The node type string this handler processes (e.g. "send_message").</summary>
    string NodeType { get; }

    /// <summary>
    /// Executes the node and returns the result indicating the next edge condition
    /// and whether the engine should pause and wait for an inbound message.
    /// </summary>
    Task<FlowNodeResult> ExecuteAsync(
        FlowNode node,
        FlowExecution execution,
        MessageEnvelope? inboundMessage,
        CancellationToken ct);
}

/// <summary>
/// The outcome of a node execution.
/// </summary>
/// <param name="NextEdgeCondition">
/// The condition label used to select the next edge, or <c>null</c> if execution should stop at this node.
/// </param>
/// <param name="WaitForInput">
/// When <c>true</c> the engine pauses and waits for the next inbound message before continuing.
/// </param>
public sealed record FlowNodeResult(string? NextEdgeCondition, bool WaitForInput);
