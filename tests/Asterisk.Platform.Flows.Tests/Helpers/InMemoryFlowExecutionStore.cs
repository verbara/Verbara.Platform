using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;

namespace Asterisk.Platform.Flows.Tests.Helpers;

internal sealed class InMemoryFlowExecutionStore : IFlowExecutionStore
{
    private readonly Dictionary<string, FlowExecution> _store = new(StringComparer.Ordinal);

    public Task SaveAsync(FlowExecution execution, CancellationToken ct)
    {
        _store[execution.ExecutionId.Value] = execution;
        return Task.CompletedTask;
    }

    public Task<FlowExecution?> GetByIdAsync(EntityId executionId, CancellationToken ct) =>
        Task.FromResult(_store.TryGetValue(executionId.Value, out var e) ? e : null);

    public Task<FlowExecution?> GetActiveByConversationAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var match = null as FlowExecution;
        foreach (var ex in _store.Values)
        {
            if (ex.TenantId == tenantId && ex.ConversationId == conversationId &&
                ex.Status is FlowExecutionStatus.Running or FlowExecutionStatus.WaitingForInput)
            {
                match = ex;
                break;
            }
        }
        return Task.FromResult(match);
    }
}
