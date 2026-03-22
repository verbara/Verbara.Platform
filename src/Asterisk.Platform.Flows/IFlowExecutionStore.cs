using Asterisk.Platform.Core;

namespace Asterisk.Platform.Flows;

/// <summary>
/// Persistence contract for flow execution state.
/// </summary>
public interface IFlowExecutionStore
{
    /// <summary>Persists an execution (insert or update).</summary>
    Task SaveAsync(FlowExecution execution, CancellationToken ct);

    /// <summary>Retrieves an execution by its ID.</summary>
    Task<FlowExecution?> GetByIdAsync(EntityId executionId, CancellationToken ct);

    /// <summary>Returns the active (Running or WaitingForInput) execution for a conversation, if any.</summary>
    Task<FlowExecution?> GetActiveByConversationAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);
}
