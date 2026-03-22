using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Flows.Nodes;

/// <summary>
/// Node type: <c>wait</c>.
/// Pauses execution and waits for the next inbound message.
/// In v1.0 the delay config key is recorded but not actively enforced — the node simply
/// yields control back to the caller by returning <see cref="FlowNodeResult.WaitForInput"/> = true.
/// </summary>
internal sealed class WaitNodeHandler : IFlowNodeHandler
{
    public string NodeType => "wait";

    public Task<FlowNodeResult> ExecuteAsync(
        FlowNode node,
        FlowExecution execution,
        MessageEnvelope? inboundMessage,
        CancellationToken ct)
    {
        // If there is already an inbound message, continue immediately.
        if (inboundMessage is not null)
            return Task.FromResult(new FlowNodeResult(NextEdgeCondition: "default", WaitForInput: false));

        return Task.FromResult(new FlowNodeResult(NextEdgeCondition: null, WaitForInput: true));
    }
}
