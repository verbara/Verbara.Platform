using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Flows.Nodes;

/// <summary>
/// Node type: <c>condition</c>.
/// Evaluates a simple <c>expression</c> of the form <c>{{var}} == "value"</c>
/// and returns the matching edge condition ("yes" or "no").
/// </summary>
internal sealed class ConditionNodeHandler : IFlowNodeHandler
{
    private readonly ITemplateRenderer _renderer;

    public ConditionNodeHandler(ITemplateRenderer renderer)
    {
        _renderer = renderer;
    }

    public string NodeType => "condition";

    public Task<FlowNodeResult> ExecuteAsync(
        FlowNode node,
        FlowExecution execution,
        MessageEnvelope? inboundMessage,
        CancellationToken ct)
    {
        node.Config.TryGetValue("expression", out var expression);

        var result = EvaluateExpression(expression ?? string.Empty, execution.Variables);
        var condition = result ? "yes" : "no";

        return Task.FromResult(new FlowNodeResult(NextEdgeCondition: condition, WaitForInput: false));
    }

    private static bool EvaluateExpression(string expression, IReadOnlyDictionary<string, string> variables)
    {
        // Render variable placeholders first.
        var rendered = RenderExpression(expression, variables);

        // Support: left == "right"  and  left != "right"
        var eqIdx = rendered.IndexOf("==", StringComparison.Ordinal);
        if (eqIdx >= 0)
        {
            var left = rendered[..eqIdx].Trim();
            var right = rendered[(eqIdx + 2)..].Trim().Trim('"');
            return string.Equals(left, right, StringComparison.Ordinal);
        }

        var neqIdx = rendered.IndexOf("!=", StringComparison.Ordinal);
        if (neqIdx >= 0)
        {
            var left = rendered[..neqIdx].Trim();
            var right = rendered[(neqIdx + 2)..].Trim().Trim('"');
            return !string.Equals(left, right, StringComparison.Ordinal);
        }

        // Fallback: non-empty string is truthy.
        return !string.IsNullOrEmpty(rendered);
    }

    /// <summary>Substitutes {{var}} placeholders in the expression without depending on ITemplateRenderer to keep this static.</summary>
    private static string RenderExpression(string expression, IReadOnlyDictionary<string, string> variables)
    {
        if (!expression.Contains("{{", StringComparison.Ordinal))
            return expression;

        var result = expression;
        foreach (var kv in variables)
        {
            if (!kv.Key.StartsWith("__", StringComparison.Ordinal))
                result = result.Replace("{{" + kv.Key + "}}", kv.Value, StringComparison.Ordinal);
        }

        return result;
    }
}
