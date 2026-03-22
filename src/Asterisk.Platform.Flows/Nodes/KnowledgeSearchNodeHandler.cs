using Asterisk.Platform.Conversations;
using Asterisk.Platform.KnowledgeBase;

namespace Asterisk.Platform.Flows.Nodes;

/// <summary>
/// Node type: <c>knowledge_search</c>.
/// Searches the knowledge base using the rendered <c>query</c> config entry and stores serialised
/// results in <c>execution.Variables</c>:
/// <list type="bullet">
///   <item><c>kb_result_count</c> — number of results returned.</item>
///   <item><c>kb_result_0_title</c>, <c>kb_result_0_content</c>, … — per-result fields.</item>
///   <item><c>kb_top_title</c>, <c>kb_top_content</c> — shorthand for the first result (if any).</item>
/// </list>
/// </summary>
internal sealed class KnowledgeSearchNodeHandler : IFlowNodeHandler
{
    private readonly IKnowledgeSearch _search;
    private readonly ITemplateRenderer _renderer;

    public KnowledgeSearchNodeHandler(IKnowledgeSearch search, ITemplateRenderer renderer)
    {
        _search = search;
        _renderer = renderer;
    }

    public string NodeType => "knowledge_search";

    public async Task<FlowNodeResult> ExecuteAsync(
        FlowNode node,
        FlowExecution execution,
        MessageEnvelope? inboundMessage,
        CancellationToken ct)
    {
        node.Config.TryGetValue("query", out var queryTemplate);
        node.Config.TryGetValue("max_results", out var maxResultsStr);

        var query = _renderer.Render(queryTemplate ?? string.Empty, execution.Variables);
        var maxResults = int.TryParse(maxResultsStr, out var mr) ? mr : 5;

        var results = await _search.SearchAsync(execution.TenantId, query, maxResults, ct).ConfigureAwait(false);

        execution.Variables["kb_result_count"] = results.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < results.Count; i++)
        {
            execution.Variables[$"kb_result_{i}_title"] = results[i].Article.Title;
            execution.Variables[$"kb_result_{i}_content"] = results[i].Article.Content;
            execution.Variables[$"kb_result_{i}_score"] = results[i].Score.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (results.Count > 0)
        {
            execution.Variables["kb_top_title"] = results[0].Article.Title;
            execution.Variables["kb_top_content"] = results[0].Article.Content;
        }

        return new FlowNodeResult(
            NextEdgeCondition: results.Count > 0 ? "found" : "not_found",
            WaitForInput: false);
    }
}
