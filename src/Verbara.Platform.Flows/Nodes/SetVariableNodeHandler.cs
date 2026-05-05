using Verbara.Platform.Conversations;

namespace Verbara.Platform.Flows.Nodes;

/// <summary>
/// Node type: <c>set_variable</c>.
/// Sets <c>execution.Variables[variable]</c> to the rendered <c>value</c> config entry.
/// </summary>
internal sealed class SetVariableNodeHandler : IFlowNodeHandler
{
    private readonly ITemplateRenderer _renderer;

    public SetVariableNodeHandler(ITemplateRenderer renderer)
    {
        _renderer = renderer;
    }

    public string NodeType => "set_variable";

    public Task<FlowNodeResult> ExecuteAsync(
        FlowNode node,
        FlowExecution execution,
        MessageEnvelope? inboundMessage,
        CancellationToken ct)
    {
        node.Config.TryGetValue("variable", out var variable);
        node.Config.TryGetValue("value", out var valueTemplate);

        if (!string.IsNullOrEmpty(variable))
        {
            var rendered = _renderer.Render(valueTemplate ?? string.Empty, execution.Variables);
            execution.Variables[variable] = rendered;
        }

        return Task.FromResult(new FlowNodeResult(NextEdgeCondition: "default", WaitForInput: false));
    }
}
