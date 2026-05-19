namespace Verbara.Platform.Realtime.Contracts.Dtos;

/// <summary>
/// Response payload for <c>GET /api/v1/internal/agent-tenant/{agentId}</c> on
/// Verbara.Platform.Api. Realtime uses this to resolve the tenant that owns
/// an agent connection before authorizing cross-tenant subscriptions.
/// </summary>
/// <param name="AgentId">The agent identifier requested (echoed for client-side cache keying).</param>
/// <param name="TenantId">The resolved tenant identifier.</param>
/// <param name="ResolvedAt">UTC timestamp when Platform.Api computed the resolution. Realtime client uses this to honour its 5-minute local IMemoryCache TTL.</param>
public sealed record AgentTenantResponse(
    string AgentId,
    string TenantId,
    DateTimeOffset ResolvedAt);
