using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Flows;

/// <summary>
/// Drives flow execution: starts executions, processes inbound messages, and reports status.
/// </summary>
public interface IFlowExecutionEngine
{
    /// <summary>
    /// Starts a new execution for the published version of the specified flow,
    /// binding it to the given conversation.
    /// </summary>
    Task<FlowExecution> StartAsync(
        TenantId tenantId,
        EntityId flowId,
        EntityId conversationId,
        CancellationToken ct);

    /// <summary>
    /// Advances the active execution for the given execution ID by processing an inbound message.
    /// The engine walks the DAG until a WaitForInput node or a terminal node is reached.
    /// </summary>
    Task<FlowStepResult> ProcessMessageAsync(
        EntityId executionId,
        MessageEnvelope message,
        CancellationToken ct);

    /// <summary>
    /// Returns the active (Running or WaitingForInput) execution for the conversation, or <c>null</c>.
    /// </summary>
    Task<FlowExecution?> GetActiveExecutionAsync(
        TenantId tenantId,
        EntityId conversationId,
        CancellationToken ct);
}

/// <summary>
/// The result of one <see cref="IFlowExecutionEngine.ProcessMessageAsync"/> call.
/// </summary>
/// <param name="Status">The execution status after processing.</param>
/// <param name="OutboundMessages">Messages produced by send_message nodes during this step.</param>
/// <param name="TargetQueueId">The queue to hand off to, populated when status is <see cref="FlowExecutionStatus.HandedOff"/>.</param>
/// <param name="Priority">The handoff priority, populated alongside <paramref name="TargetQueueId"/>.</param>
/// <param name="FlowMetadata">
/// Non-private flow variables collected during the run (keys not prefixed with <c>__</c>),
/// so downstream consumers (e.g. wrap-up pre-fill) can read what the bot captured. May be empty.
/// </param>
public sealed record FlowStepResult(
    FlowExecutionStatus Status,
    IReadOnlyList<MessageEnvelope>? OutboundMessages,
    EntityId? TargetQueueId,
    MessagePriority? Priority,
    IReadOnlyDictionary<string, string>? FlowMetadata = null);
