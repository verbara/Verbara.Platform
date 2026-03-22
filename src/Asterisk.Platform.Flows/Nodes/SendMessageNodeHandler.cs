using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Flows.Nodes;

/// <summary>
/// Node type: <c>send_message</c>.
/// Renders the <c>template</c> config key against execution variables and enqueues an outbound message.
/// The rendered text is stored in the execution's <c>__last_outbound</c> variable so callers can collect it.
/// </summary>
internal sealed class SendMessageNodeHandler : IFlowNodeHandler
{
    private readonly ITemplateRenderer _renderer;

    public SendMessageNodeHandler(ITemplateRenderer renderer)
    {
        _renderer = renderer;
    }

    public string NodeType => "send_message";

    public Task<FlowNodeResult> ExecuteAsync(
        FlowNode node,
        FlowExecution execution,
        MessageEnvelope? inboundMessage,
        CancellationToken ct)
    {
        node.Config.TryGetValue("template", out var template);
        var rendered = _renderer.Render(template ?? string.Empty, execution.Variables);

        // Store the rendered message so the engine can collect outbound messages.
        execution.Variables["__last_outbound"] = rendered;

        // Convention: outbound key per step so the engine can reconstruct the list.
        var key = $"__outbound_{execution.StepCount}";
        execution.Variables[key] = rendered;

        return Task.FromResult(new FlowNodeResult(NextEdgeCondition: "default", WaitForInput: false));
    }
}
