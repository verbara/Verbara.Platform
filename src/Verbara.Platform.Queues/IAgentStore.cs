using Verbara.Platform.Core;

namespace Verbara.Platform.Queues;

public interface IAgentStore
{
    Task<Agent?> GetByIdAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task<Agent?> GetByUserIdAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
    Task<Agent?> GetByExtensionAsync(TenantId tenantId, string extension, CancellationToken ct);
    Task<PagedResult<Agent>> ListAsync(TenantId tenantId, AgentQuery query, CancellationToken ct);
    Task SaveAsync(Agent agent, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);
}

public sealed record AgentQuery
{
    public AgentState? State { get; init; }
    public EntityId? TeamId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}
