using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Flows;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryFlowStore : IFlowStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), FlowDefinition> _items = new();

    public Task<IReadOnlyList<FlowDefinition>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        var result = _items.Values.Where(f => f.TenantId == tenantId).ToList();
        return Task.FromResult<IReadOnlyList<FlowDefinition>>(result);
    }

    public Task<FlowDefinition?> GetByIdAsync(TenantId tenantId, EntityId flowId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, flowId), out var item);
        return Task.FromResult(item);
    }

    public Task<FlowDefinition?> GetPublishedAsync(TenantId tenantId, EntityId flowId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, flowId), out var item);
        var result = item?.IsPublished == true ? item : null;
        return Task.FromResult(result);
    }

    public Task SaveAsync(FlowDefinition flow, CancellationToken ct)
    {
        _items[(flow.TenantId, flow.FlowId)] = flow;
        return Task.CompletedTask;
    }
}
