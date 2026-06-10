using Verbara.Platform.Conversations;
using Verbara.Platform.Llm;

namespace Verbara.Platform.Flows.Nodes;

/// <summary>
/// Node type: <c>ai_classify</c>.
/// Sends a classification prompt to <see cref="ILlmProvider"/> and returns the edge condition
/// that matches the classified label.
/// Config keys: <c>system_prompt</c>, <c>labels</c> (comma-separated), <c>variable</c> (optional, stores result).
/// </summary>
internal sealed class AiClassifyNodeHandler : IFlowNodeHandler
{
    private readonly ILlmProvider _llm;
    private readonly ITemplateRenderer _renderer;

    public AiClassifyNodeHandler(ILlmProvider llm, ITemplateRenderer renderer)
    {
        _llm = llm;
        _renderer = renderer;
    }

    public string NodeType => "ai_classify";

    public async Task<FlowNodeResult> ExecuteAsync(
        FlowNode node,
        FlowExecution execution,
        MessageEnvelope? inboundMessage,
        CancellationToken ct)
    {
        node.Config.TryGetValue("system_prompt", out var systemPrompt);
        node.Config.TryGetValue("labels", out var labelsRaw);
        node.Config.TryGetValue("variable", out var variable);

        var renderedPrompt = _renderer.Render(systemPrompt ?? string.Empty, execution.Variables);

        var userContent = ExtractText(inboundMessage) ?? execution.Variables.GetValueOrDefault("__last_outbound", string.Empty);

        var messages = new List<LlmMessage>
        {
            new("system", renderedPrompt),
            new("user", userContent),
        };

        var response = await _llm.CompleteAsync(new LlmRequest(messages), ct).ConfigureAwait(false);

        var label = response.Content.Trim().ToLowerInvariant();

        if (!string.IsNullOrEmpty(variable))
            execution.Variables[variable] = label;

        return new FlowNodeResult(NextEdgeCondition: label, WaitForInput: false);
    }

    private static string? ExtractText(MessageEnvelope? envelope)
    {
        if (envelope is null) return null;
        foreach (var block in envelope.Blocks)
        {
            if (block is TextBlock tb) return tb.Text;
        }
        return null;
    }
}
