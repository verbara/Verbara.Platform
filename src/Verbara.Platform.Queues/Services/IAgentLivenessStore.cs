using Verbara.Platform.Core;

namespace Verbara.Platform.Queues.Services;

/// <summary>
/// W3 server-side agent liveness. A routable agent's browser proves it is
/// alive by periodically refreshing a presence key with a short TTL; the
/// liveness reaper forces Offline any routable agent whose key is missing.
/// Presence is determined SOLELY by key existence — the stored value is
/// diagnostic. Transport-agnostic (works for SSE-only and SignalR agents).
/// </summary>
public interface IAgentLivenessStore
{
    Task TouchAsync(TenantId tenantId, EntityId agentId, TimeSpan ttl, string nodeId, CancellationToken ct);
    Task<bool> IsAliveAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);
    Task RemoveAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);
}
