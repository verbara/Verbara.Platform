using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;

namespace Asterisk.Platform.Flows.Tests.Helpers;

internal sealed class InMemoryFlowStore : IFlowStore
{
    private readonly List<FlowDefinition> _flows = new();

    public void Add(FlowDefinition flow) => _flows.Add(flow);

    public Task<FlowDefinition?> GetByIdAsync(TenantId tenantId, EntityId flowId, CancellationToken ct) =>
        Task.FromResult(_flows.Find(f => f.TenantId == tenantId && f.FlowId == flowId));

    public Task<FlowDefinition?> GetPublishedAsync(TenantId tenantId, EntityId flowId, CancellationToken ct) =>
        Task.FromResult(_flows.Find(f => f.TenantId == tenantId && f.FlowId == flowId && f.IsPublished));

    public Task SaveAsync(FlowDefinition flow, CancellationToken ct)
    {
        _flows.RemoveAll(f => f.FlowId == flow.FlowId);
        _flows.Add(flow);
        return Task.CompletedTask;
    }
}
