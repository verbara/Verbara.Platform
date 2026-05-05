using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Flows;

/// <summary>
/// Default implementation of <see cref="IFlowExecutionEngine"/>.
/// Walks the DAG one step at a time, collecting outbound messages, until the flow pauses or terminates.
/// </summary>
internal sealed partial class FlowExecutionEngine : IFlowExecutionEngine
{
    private const int MaxStepsPerCall = 1000;

    private readonly IFlowStore _flowStore;
    private readonly IFlowExecutionStore _executionStore;
    private readonly Dictionary<string, IFlowNodeHandler> _handlers;
    private readonly ILogger<FlowExecutionEngine> _logger;

    public FlowExecutionEngine(
        IFlowStore flowStore,
        IFlowExecutionStore executionStore,
        IEnumerable<IFlowNodeHandler> handlers,
        ILogger<FlowExecutionEngine> logger)
    {
        _flowStore = flowStore;
        _executionStore = executionStore;
        _logger = logger;

        _handlers = new Dictionary<string, IFlowNodeHandler>(StringComparer.Ordinal);
        foreach (var h in handlers)
            _handlers[h.NodeType] = h;
    }

    /// <inheritdoc />
    public async Task<FlowExecution> StartAsync(
        TenantId tenantId,
        EntityId flowId,
        EntityId conversationId,
        CancellationToken ct)
    {
        var flow = await _flowStore.GetPublishedAsync(tenantId, flowId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No published flow found for id '{flowId}' in tenant '{tenantId}'.");

        var execution = new FlowExecution
        {
            ExecutionId = EntityId.New(),
            FlowId = flowId,
            FlowVersion = flow.Version,
            TenantId = tenantId,
            ConversationId = conversationId,
            CurrentNodeId = flow.EntryNodeId,
            Status = FlowExecutionStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
        };

        await _executionStore.SaveAsync(execution, ct).ConfigureAwait(false);
        LogExecutionStarted(execution.ExecutionId.Value, flowId.Value);
        return execution;
    }

    /// <inheritdoc />
    public async Task<FlowStepResult> ProcessMessageAsync(
        EntityId executionId,
        MessageEnvelope message,
        CancellationToken ct)
    {
        var execution = await _executionStore.GetByIdAsync(executionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Execution '{executionId}' not found.");

        if (execution.Status is not (FlowExecutionStatus.Running or FlowExecutionStatus.WaitingForInput))
        {
            return new FlowStepResult(execution.Status, null, null, null);
        }

        var flow = await _flowStore.GetByIdAsync(execution.TenantId, execution.FlowId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Flow '{execution.FlowId}' not found.");

        var nodeMap = BuildNodeMap(flow);
        var outboundKeys = new List<string>();
        var steps = 0;
        MessageEnvelope? currentMessage = message;

        execution.Status = FlowExecutionStatus.Running;

        while (steps < MaxStepsPerCall)
        {
            if (!nodeMap.TryGetValue(execution.CurrentNodeId.Value, out var node))
            {
                LogNodeNotFound(execution.CurrentNodeId.Value, execution.FlowId.Value);
                execution.Status = FlowExecutionStatus.Failed;
                break;
            }

            if (!_handlers.TryGetValue(node.Type, out var handler))
                throw new InvalidOperationException($"No handler registered for node type '{node.Type}'.");

            execution.StepCount++;
            steps++;

            var result = await handler.ExecuteAsync(node, execution, currentMessage, ct).ConfigureAwait(false);

            // Collect any new outbound keys added by this node.
            CollectNewOutboundKeys(execution, outboundKeys);

            // After first node, clear inbound message so subsequent nodes don't see it.
            currentMessage = null;

            if (result.WaitForInput || execution.Status is FlowExecutionStatus.Completed or FlowExecutionStatus.HandedOff or FlowExecutionStatus.Failed)
            {
                if (result.WaitForInput)
                    execution.Status = FlowExecutionStatus.WaitingForInput;

                break;
            }

            // Advance to next node.
            if (result.NextEdgeCondition is null)
                break;

            var nextEdge = FindEdge(node, result.NextEdgeCondition);
            if (nextEdge is null)
            {
                LogNoMatchingEdge(result.NextEdgeCondition, node.NodeId.Value);
                break;
            }

            execution.CurrentNodeId = nextEdge.TargetNodeId;
        }

        if (steps >= MaxStepsPerCall)
        {
            LogMaxStepsReached(executionId.Value);
            execution.Status = FlowExecutionStatus.Failed;
        }

        await _executionStore.SaveAsync(execution, ct).ConfigureAwait(false);

        var outbound = BuildOutboundMessages(execution, outboundKeys);
        var queueId = TryGetQueueId(execution);
        var priority = TryGetPriority(execution);

        return new FlowStepResult(execution.Status, outbound, queueId, priority);
    }

    /// <inheritdoc />
    public Task<FlowExecution?> GetActiveExecutionAsync(
        TenantId tenantId,
        EntityId conversationId,
        CancellationToken ct)
        => _executionStore.GetActiveByConversationAsync(tenantId, conversationId, ct);

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Dictionary<string, FlowNode> BuildNodeMap(FlowDefinition flow)
    {
        var map = new Dictionary<string, FlowNode>(flow.Nodes.Count, StringComparer.Ordinal);
        foreach (var n in flow.Nodes)
            map[n.NodeId.Value] = n;
        return map;
    }

    private static FlowEdge? FindEdge(FlowNode node, string condition)
    {
        // Prefer exact match; fall back to "default" if present.
        FlowEdge? fallback = null;
        foreach (var edge in node.Edges)
        {
            if (string.Equals(edge.Condition, condition, StringComparison.OrdinalIgnoreCase))
                return edge;
            if (string.Equals(edge.Condition, "default", StringComparison.OrdinalIgnoreCase))
                fallback = edge;
        }
        return fallback;
    }

    private static void CollectNewOutboundKeys(FlowExecution execution, List<string> collected)
    {
        foreach (var key in execution.Variables.Keys)
        {
            if (key.StartsWith("__outbound_", StringComparison.Ordinal) && !collected.Contains(key))
                collected.Add(key);
        }
    }

    private static List<MessageEnvelope>? BuildOutboundMessages(FlowExecution execution, List<string> keys)
    {
        if (keys.Count == 0) return null;

        var messages = new List<MessageEnvelope>(keys.Count);
        foreach (var key in keys)
        {
            if (execution.Variables.TryGetValue(key, out var text) && !string.IsNullOrEmpty(text))
                messages.Add(new MessageEnvelope([new TextBlock(text)]));
        }

        return messages.Count > 0 ? messages : null;
    }

    private static EntityId? TryGetQueueId(FlowExecution execution)
    {
        if (execution.Variables.TryGetValue("__enqueue_queue_id", out var id) && !string.IsNullOrEmpty(id))
            return EntityId.From(id);
        return null;
    }

    private static MessagePriority? TryGetPriority(FlowExecution execution)
    {
        if (!execution.Variables.TryGetValue("__enqueue_priority", out var p)) return null;
        return Enum.TryParse<MessagePriority>(p, ignoreCase: true, out var priority) ? priority : null;
    }

    // ── LoggerMessage delegates ───────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Flow execution {ExecutionId} started for flow {FlowId}")]
    private partial void LogExecutionStarted(string executionId, string flowId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Node '{NodeId}' not found in flow '{FlowId}'")]
    private partial void LogNodeNotFound(string nodeId, string flowId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No edge with condition '{Condition}' from node '{NodeId}'")]
    private partial void LogNoMatchingEdge(string condition, string nodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flow execution {ExecutionId} hit max step limit")]
    private partial void LogMaxStepsReached(string executionId);
}
