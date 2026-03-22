using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Flows.Nodes;

/// <summary>
/// Node type: <c>ai_generate</c>.
/// Calls <see cref="ILlmProvider"/> with a rendered <c>system_prompt</c> and conversation context,
/// then stores the response as an outbound message.
/// </summary>
internal sealed class AiGenerateNodeHandler : IFlowNodeHandler
{
    private readonly ILlmProvider _llm;
    private readonly ITemplateRenderer _renderer;

    public AiGenerateNodeHandler(ILlmProvider llm, ITemplateRenderer renderer)
    {
        _llm = llm;
        _renderer = renderer;
    }

    public string NodeType => "ai_generate";

    public async Task<FlowNodeResult> ExecuteAsync(
        FlowNode node,
        FlowExecution execution,
        MessageEnvelope? inboundMessage,
        CancellationToken ct)
    {
        node.Config.TryGetValue("system_prompt", out var systemPrompt);

        var renderedSystem = _renderer.Render(systemPrompt ?? string.Empty, execution.Variables);
        var userContent = ExtractText(inboundMessage) ?? string.Empty;

        var messages = new List<LlmMessage>
        {
            new("system", renderedSystem),
            new("user", userContent),
        };

        var response = await _llm.CompleteAsync(new LlmRequest(messages), ct).ConfigureAwait(false);

        var key = $"__outbound_{execution.StepCount}";
        execution.Variables[key] = response.Content;
        execution.Variables["__last_outbound"] = response.Content;

        return new FlowNodeResult(NextEdgeCondition: "default", WaitForInput: false);
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
