using System.Text.RegularExpressions;
using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Flows.Nodes;

/// <summary>
/// Node type: <c>collect_input</c>.
/// Waits for an inbound text message, validates it against an optional <c>validator</c> regex,
/// and stores the value in <c>execution.Variables[variable]</c>.
/// If validation fails and retries remain, it sends the <c>retry_prompt</c> and waits again.
/// </summary>
internal sealed class CollectInputNodeHandler : IFlowNodeHandler
{
    private readonly ITemplateRenderer _renderer;

    public CollectInputNodeHandler(ITemplateRenderer renderer)
    {
        _renderer = renderer;
    }

    public string NodeType => "collect_input";

    public Task<FlowNodeResult> ExecuteAsync(
        FlowNode node,
        FlowExecution execution,
        MessageEnvelope? inboundMessage,
        CancellationToken ct)
    {
        // No message yet — wait.
        if (inboundMessage is null)
            return Task.FromResult(new FlowNodeResult(NextEdgeCondition: null, WaitForInput: true));

        // Extract text from the first text block.
        var text = string.Empty;
        foreach (var block in inboundMessage.Blocks)
        {
            if (block is TextBlock tb)
            {
                text = tb.Text;
                break;
            }
        }

        node.Config.TryGetValue("validator", out var validator);
        node.Config.TryGetValue("variable", out var variable);
        node.Config.TryGetValue("retry_prompt", out var retryPrompt);
        node.Config.TryGetValue("max_retries", out var maxRetriesStr);

        var maxRetries = int.TryParse(maxRetriesStr, out var mr) ? mr : 3;
        var retryKey = $"__collect_retries_{node.NodeId}";
        var retries = execution.Variables.TryGetValue(retryKey, out var rv) && int.TryParse(rv, out var r) ? r : 0;

        // Validate.
        var valid = string.IsNullOrEmpty(validator) || Regex.IsMatch(text, validator);

        if (!valid && retries < maxRetries)
        {
            execution.Variables[retryKey] = (retries + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Emit retry prompt as an outbound message.
            var rendered = _renderer.Render(retryPrompt ?? "Invalid input. Please try again.", execution.Variables);
            var key = $"__outbound_{execution.StepCount}";
            execution.Variables[key] = rendered;

            return Task.FromResult(new FlowNodeResult(NextEdgeCondition: null, WaitForInput: true));
        }

        // Success — store the value and clear retry counter.
        if (!string.IsNullOrEmpty(variable))
            execution.Variables[variable] = text;

        execution.Variables.Remove(retryKey);

        return Task.FromResult(new FlowNodeResult(NextEdgeCondition: "collected", WaitForInput: false));
    }
}
