using Verbara.Platform.Core;

namespace Verbara.Platform.Queues.Services;

/// <summary>
/// Resolves an agent's EFFECTIVE channel capacity = per-agent override merged over the tenant
/// default (W6), read at call time. Owns the agent + defaults read so callers don't double-fetch.
/// </summary>
public interface IAgentCapacityResolver
{
    /// <summary>
    /// Returns the agent's EFFECTIVE capacity, or <c>null</c> when the agent does not exist
    /// (the resolver owns the single agent read AND doubles as the existence signal).
    /// </summary>
    Task<ChannelCapacity?> ResolveAsync(TenantId tenantId, EntityId agentId, CancellationToken ct);
}
