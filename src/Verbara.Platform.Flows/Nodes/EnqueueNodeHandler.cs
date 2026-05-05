using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Flows.Nodes;

/// <summary>
/// Node type: <c>enqueue</c>.
/// Sets the execution status to <see cref="FlowExecutionStatus.HandedOff"/> and stores the
/// <c>queue_id</c> and <c>priority</c> in execution variables so the engine can report them
/// in the <see cref="FlowStepResult"/>.
/// </summary>
internal sealed class EnqueueNodeHandler : IFlowNodeHandler
{
    public string NodeType => "enqueue";

    public Task<FlowNodeResult> ExecuteAsync(
        FlowNode node,
        FlowExecution execution,
        MessageEnvelope? inboundMessage,
        CancellationToken ct)
    {
        node.Config.TryGetValue("queue_id", out var queueId);
        node.Config.TryGetValue("priority", out var priority);

        execution.Status = FlowExecutionStatus.HandedOff;
        execution.CompletedAt = DateTimeOffset.UtcNow;

        if (!string.IsNullOrEmpty(queueId))
            execution.Variables["__enqueue_queue_id"] = queueId;

        if (!string.IsNullOrEmpty(priority))
            execution.Variables["__enqueue_priority"] = priority;

        return Task.FromResult(new FlowNodeResult(NextEdgeCondition: null, WaitForInput: false));
    }
}
