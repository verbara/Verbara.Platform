using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryFlowExecutionStore : IFlowExecutionStore
{
    private readonly ConcurrentDictionary<EntityId, FlowExecution> _items = new();

    public Task SaveAsync(FlowExecution execution, CancellationToken ct)
    {
        _items[execution.ExecutionId] = execution;
        return Task.CompletedTask;
    }

    public Task<FlowExecution?> GetByIdAsync(EntityId executionId, CancellationToken ct)
    {
        _items.TryGetValue(executionId, out var item);
        return Task.FromResult(item);
    }

    public Task<FlowExecution?> GetActiveByConversationAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(e =>
            e.TenantId == tenantId &&
            e.ConversationId == conversationId &&
            (e.Status == FlowExecutionStatus.Running || e.Status == FlowExecutionStatus.WaitingForInput));

        return Task.FromResult(result);
    }
}
