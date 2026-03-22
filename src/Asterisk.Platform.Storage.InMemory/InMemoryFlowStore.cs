using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryFlowStore : IFlowStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), FlowDefinition> _items = new();

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
