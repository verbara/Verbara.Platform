using System.Globalization;
using System.Text;
using System.Text.Json;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Flows.Nodes;

/// <summary>
/// Node type: <c>collect_reason</c>.
/// Walks the shared, published typification taxonomy as a numbered IVR/bot menu and
/// writes the captured root→leaf path into the flow's <c>reasonPath</c> variable
/// (a JSON array of node <see cref="TypificationNode.Code"/>s) for the wrap-up prefill
/// resolver to consume.
/// <para>
/// Config keys:
/// <list type="bullet">
///   <item><c>schema_id</c> (required) — the typification schema to walk.</item>
///   <item><c>subtree_root_node_id</c> (optional) — start the menu at this node's
///   children instead of the true roots. The captured <c>reasonPath</c> is STILL the
///   full path from the true root (so the prefill resolver, which validates from root,
///   accepts it).</item>
///   <item><c>prompt</c> (optional) — intro text rendered above the first menu.</item>
///   <item><c>retry_prompt</c> (optional) — shown on an invalid selection.</item>
///   <item><c>max_retries</c> (optional, default 3) — invalid-selection cap; on exhaustion
///   the node yields the <c>error</c> edge.</item>
/// </list>
/// </para>
/// <para>
/// Cascade state lives in private (double-underscore) execution variables keyed by the
/// flow node id: <c>__reason_current_&lt;nodeId&gt;</c> holds the current cascade position
/// (absent/empty = at the subtree root, or the true root when no subtree is configured),
/// and <c>__reason_retries_&lt;nodeId&gt;</c> is the invalid-selection counter. Both are
/// cleared once a leaf is captured.
/// </para>
/// </summary>
internal sealed class CollectReasonNodeHandler : IFlowNodeHandler
{
    private const int DefaultMaxRetries = 3;

    private readonly ITemplateRenderer _renderer;
    private readonly ITypificationSchemaStore _schemaStore;
    private readonly IConversationStore _conversationStore;

    public CollectReasonNodeHandler(
        ITemplateRenderer renderer,
        ITypificationSchemaStore schemaStore,
        IConversationStore conversationStore)
    {
        _renderer = renderer;
        _schemaStore = schemaStore;
        _conversationStore = conversationStore;
    }

    public string NodeType => "collect_reason";

    public async Task<FlowNodeResult> ExecuteAsync(
        FlowNode node,
        FlowExecution execution,
        MessageEnvelope? inboundMessage,
        CancellationToken ct)
    {
        // --- Config ---------------------------------------------------------
        if (!node.Config.TryGetValue("schema_id", out var schemaIdRaw) || !EntityId.IsValid(schemaIdRaw))
        {
            // Unrecoverable config issue — keep it from throwing, take the error edge.
            return new FlowNodeResult(NextEdgeCondition: "error", WaitForInput: false);
        }

        var schemaId = EntityId.From(schemaIdRaw);

        EntityId? subtreeRoot = null;
        if (node.Config.TryGetValue("subtree_root_node_id", out var subtreeRaw) && EntityId.IsValid(subtreeRaw))
            subtreeRoot = EntityId.From(subtreeRaw);

        // --- Load the published schema -------------------------------------
        var schema = await _schemaStore
            .GetLatestPublishedAsync(execution.TenantId, schemaId, ct)
            .ConfigureAwait(false);

        if (schema is null)
        {
            EmitOutbound(execution, "The selection menu is unavailable right now.");
            return new FlowNodeResult(NextEdgeCondition: "error", WaitForInput: false);
        }

        // --- Channel filtering ---------------------------------------------
        // Present only nodes whose ChannelApplicability is null (all channels) OR
        // contains the conversation's channel. The conversation is loaded cheaply by id;
        // if it cannot be resolved (e.g. a transient store miss) we degrade to "all
        // channels apply" so the menu still renders rather than going dark.
        var conversation = await _conversationStore
            .GetByIdAsync(execution.TenantId, execution.ConversationId, ct)
            .ConfigureAwait(false);
        var channel = conversation?.Channel;

        var currentKey = $"__reason_current_{execution.CurrentNodeId}";
        var retryKey = $"__reason_retries_{execution.CurrentNodeId}";

        // The current cascade position: a node id, or null when at the start
        // (subtree root if configured, else the true roots).
        EntityId? currentPosition = null;
        if (execution.Variables.TryGetValue(currentKey, out var currentRaw) && EntityId.IsValid(currentRaw))
            currentPosition = EntityId.From(currentRaw);
        else
            currentPosition = subtreeRoot;

        // --- First entry: render the top-level menu, wait for input ---------
        if (inboundMessage is null)
        {
            var children = ChildrenOf(schema, currentPosition, channel);
            if (children.Count == 0)
            {
                // Nothing to present (empty taxonomy / over-restrictive subtree+channel).
                EmitOutbound(execution, "No options are available.");
                return new FlowNodeResult(NextEdgeCondition: "error", WaitForInput: false);
            }

            EmitMenu(execution, node, children, includePrompt: true);
            return new FlowNodeResult(NextEdgeCondition: null, WaitForInput: true);
        }

        // --- Inbound selection ---------------------------------------------
        var currentChildren = ChildrenOf(schema, currentPosition, channel);
        var text = ExtractText(inboundMessage).Trim();

        var maxRetries = node.Config.TryGetValue("max_retries", out var mrRaw)
            && int.TryParse(mrRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mr)
            ? mr
            : DefaultMaxRetries;

        var selectionValid =
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var selection)
            && selection >= 1
            && selection <= currentChildren.Count;

        if (!selectionValid)
        {
            var retries = execution.Variables.TryGetValue(retryKey, out var rv)
                && int.TryParse(rv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)
                ? r
                : 0;

            if (retries >= maxRetries)
            {
                // Give up — clear state and take the error edge.
                execution.Variables.Remove(retryKey);
                execution.Variables.Remove(currentKey);
                return new FlowNodeResult(NextEdgeCondition: "error", WaitForInput: false);
            }

            execution.Variables[retryKey] = (retries + 1).ToString(CultureInfo.InvariantCulture);

            // Re-render the current menu (intro prompt suppressed on a retry), prefixed by
            // the configured retry hint. Both share the single per-step __outbound_<StepCount>
            // engine key, so they MUST be composed into ONE message — emitting them
            // separately would have the menu silently overwrite the retry hint.
            node.Config.TryGetValue("retry_prompt", out var retryPrompt);
            var retryHint = string.IsNullOrEmpty(retryPrompt)
                ? null
                : _renderer.Render(retryPrompt, execution.Variables);

            EmitMenu(execution, node, currentChildren, includePrompt: false, retryHint: retryHint);
            return new FlowNodeResult(NextEdgeCondition: null, WaitForInput: true);
        }

        // Valid selection. Reset the retry counter for this position.
        execution.Variables.Remove(retryKey);

        var selected = currentChildren[selection - 1];

        if (selected.IsLeaf)
        {
            // Build the FULL root→leaf Codes path (walk to the true root regardless of
            // any configured subtree root, so the prefill resolver accepts it).
            var codes = BuildRootToLeafCodes(schema, selected);
            var json = JsonSerializer.Serialize(codes, TypificationJsonContext.Default.StringArray);
            execution.Variables[TypificationMetadataKeys.ReasonPath] = json;

            execution.Variables.Remove(currentKey);
            execution.Variables.Remove(retryKey);

            return new FlowNodeResult(NextEdgeCondition: "collected", WaitForInput: false);
        }

        // Branch node — descend one level and render its (channel-filtered) children.
        execution.Variables[currentKey] = selected.NodeId.Value;

        var nextChildren = ChildrenOf(schema, selected.NodeId, channel);
        if (nextChildren.Count == 0)
        {
            // A branch with no presentable children is a misconfigured/over-filtered
            // taxonomy — fail safe rather than dead-ending the flow.
            execution.Variables.Remove(currentKey);
            EmitOutbound(execution, "No further options are available.");
            return new FlowNodeResult(NextEdgeCondition: "error", WaitForInput: false);
        }

        EmitMenu(execution, node, nextChildren, includePrompt: false);
        return new FlowNodeResult(NextEdgeCondition: null, WaitForInput: true);
    }

    /// <summary>
    /// Children of <paramref name="parentNodeId"/> (or the true roots when it is null),
    /// channel-filtered and ordered by <see cref="TypificationNode.SortOrder"/>.
    /// </summary>
    private static List<TypificationNode> ChildrenOf(
        TypificationSchema schema, EntityId? parentNodeId, ChannelType? channel)
    {
        var children = new List<TypificationNode>();
        foreach (var n in schema.Nodes)
        {
            if (n.ParentNodeId != parentNodeId)
                continue;

            if (!AppliesToChannel(n, channel))
                continue;

            children.Add(n);
        }

        children.Sort(static (a, b) => a.SortOrder.CompareTo(b.SortOrder));
        return children;
    }

    /// <summary>
    /// A node applies when it has no channel restriction (null = all channels) or when
    /// the conversation channel is among its <see cref="TypificationNode.ChannelApplicability"/>.
    /// When the channel could not be resolved, all nodes apply (fail-open render).
    /// </summary>
    private static bool AppliesToChannel(TypificationNode node, ChannelType? channel)
    {
        if (node.ChannelApplicability is null || channel is null)
            return true;

        return node.ChannelApplicability.Contains(channel.Value);
    }

    /// <summary>
    /// Walks <paramref name="leaf"/>'s parent chain up to the true root and returns the
    /// Codes ordered root→leaf. Defensive against malformed cycles (bounded by node count).
    /// </summary>
    private static string[] BuildRootToLeafCodes(TypificationSchema schema, TypificationNode leaf)
    {
        var byId = new Dictionary<EntityId, TypificationNode>(schema.Nodes.Count);
        foreach (var n in schema.Nodes)
            byId[n.NodeId] = n;

        var reversed = new List<string>();
        var current = leaf;
        var guard = 0;
        while (true)
        {
            reversed.Add(current.Code);

            if (current.ParentNodeId is not { } parentId)
                break;

            if (!byId.TryGetValue(parentId, out var parent))
                break;

            current = parent;

            if (++guard > schema.Nodes.Count)
                break; // Cycle guard — never loop forever.
        }

        reversed.Reverse();
        return [.. reversed];
    }

    /// <summary>
    /// Emits a numbered menu as a SINGLE outbound message, optionally prefixed by the config
    /// intro prompt (first entry) or a pre-rendered retry hint (invalid-selection re-prompt).
    /// Composing both into one message is required because every emit in a single handler call
    /// shares the same <c>__outbound_&lt;StepCount&gt;</c> engine key.
    /// </summary>
    private void EmitMenu(
        FlowExecution execution, FlowNode node, List<TypificationNode> children, bool includePrompt,
        string? retryHint = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(retryHint))
        {
            sb.Append(retryHint);
            sb.Append("\n\n");
        }

        if (includePrompt && node.Config.TryGetValue("prompt", out var prompt) && !string.IsNullOrEmpty(prompt))
        {
            sb.Append(_renderer.Render(prompt, execution.Variables));
            sb.Append('\n');
        }

        for (var i = 0; i < children.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');

            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture));
            sb.Append(". ");
            sb.Append(_renderer.Render(children[i].Label, execution.Variables));
        }

        EmitOutboundRaw(execution, sb.ToString());
    }

    /// <summary>Emits literal text (rendered for placeholders) as an outbound message.</summary>
    private void EmitOutbound(FlowExecution execution, string text) =>
        EmitOutboundRaw(execution, _renderer.Render(text, execution.Variables));

    /// <summary>
    /// Writes an outbound message using the engine convention: a
    /// <c>__outbound_&lt;StepCount&gt;</c> execution variable holding the message text.
    /// </summary>
    private static void EmitOutboundRaw(FlowExecution execution, string text)
    {
        var key = $"__outbound_{execution.StepCount}";
        execution.Variables[key] = text;
    }

    private static string ExtractText(MessageEnvelope inboundMessage)
    {
        foreach (var block in inboundMessage.Blocks)
        {
            if (block is TextBlock tb)
                return tb.Text;
        }

        return string.Empty;
    }
}
