using Asterisk.Platform.Core;

namespace Asterisk.Platform.Queues;

public interface IAgentStore
{
    Task<Agent?> GetByIdAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task<Agent?> GetByUserIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
    Task<PagedResult<Agent>> ListAsync(TenantId tenantId, AgentQuery query, CancellationToken ct);
    Task SaveAsync(Agent agent, CancellationToken ct);
}

public sealed record AgentQuery
{
    public AgentState? State { get; init; }
    public EntityId? TeamId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}
