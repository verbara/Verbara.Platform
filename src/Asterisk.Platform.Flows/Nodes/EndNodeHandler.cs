using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Flows.Nodes;

/// <summary>
/// Node type: <c>end</c>.
/// Sets the execution status to <see cref="FlowExecutionStatus.Completed"/> and terminates DAG traversal.
/// </summary>
internal sealed class EndNodeHandler : IFlowNodeHandler
{
    public string NodeType => "end";

    public Task<FlowNodeResult> ExecuteAsync(
        FlowNode node,
        FlowExecution execution,
        MessageEnvelope? inboundMessage,
        CancellationToken ct)
    {
        execution.Status = FlowExecutionStatus.Completed;
        execution.CompletedAt = DateTimeOffset.UtcNow;

        return Task.FromResult(new FlowNodeResult(NextEdgeCondition: null, WaitForInput: false));
    }
}
