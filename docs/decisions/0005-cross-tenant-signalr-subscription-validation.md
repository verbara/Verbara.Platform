# ADR-0005: Cross-tenant subscription validation in SignalR hubs

- **Status:** Accepted (executed during R5.2 Phase 0, 2026-04-25)
- **Date:** 2026-04-25
- **Deciders:** Harold Reina
- **Related:**
  - ADR-0002 (`docs/decisions/0002-tenant-stamping-pipeline-end-to-end.md`) — tenant stamping policy
  - ADR-0004 (`docs/decisions/0004-tenant-stamping-execution-conventions.md`) — per-package execution conventions companion
  - R5.2 spec §B.15 (`docs/plans/active/2026-04-25-r5.2-security-admin-compliance.md`)
  - Wave 2 audit B.15 (security finding 2026-04-25)

## Context

The Wave 2 audit (run 2026-04-25) discovered **B.15 — P0 (security)**: `Asterisk.Sdk.Pro.Push.SignalR.Hubs.PlatformHub.SubscribeToAgentPresenceAsync(string agentId, CancellationToken)` joins the caller to the SignalR group `presence:agent:{agentId}` without validating that the caller's tenant owns the agent.

**Concrete attack:**

1. Supervisor of tenant **A** authenticates with valid JWT containing claim `tid=A`.
2. Supervisor invokes hub method: `SubscribeToAgentPresenceAsync("agent-from-tenant-B")`.
3. Code adds the supervisor's connection to group `presence:agent:agent-from-tenant-B`.
4. Supervisor now receives **all presence events** for the agent of tenant B (status changes, device metadata, last heartbeat).

This is a cross-tenant information leak — exactly the kind of multi-tenancy vulnerability that procurement security reviews look for.

The other layers of defense (`PresenceFanoutService` group filtering, JWT `tid` validation on `OnConnectedAsync`) prevent unauthenticated access but **do not prevent cross-tenant subscription** by an authenticated supervisor of a different tenant.

**The decision space** for fixing B.15 has three candidates:

1. **JWT claim trust-only:** validate at subscribe time using `Context.User.FindFirstValue("tid")` against an `agentId`-encoded tenant prefix. Rejected because tenant membership can change after token issuance — a supervisor moved from tenant A to tenant B retains the old token until expiry, leaking access. Also encoding tenant in `agentId` is brittle.
2. **Database round-trip per call:** every `SubscribeToAgentPresenceAsync` invocation queries `agents.tenant_id`. Rejected because hot-path for connection-init bursts (10s of agents per supervisor); adds DB load.
3. **Cached resolver:** in-memory cache keyed by `agentId` with bounded TTL + lateral invalidation. Balances correctness + performance.

## Decision

**Implement `IAgentTenantResolver` with 5-minute `IMemoryCache` per-process; lateral invalidation via Pro.Push event `agent.tenant.membership.changed`.**

### Surface

```csharp
namespace Asterisk.Sdk.Pro.Push.SignalR.Authz;

public interface IAgentTenantResolver
{
    Task<string?> GetTenantIdAsync(string agentId, CancellationToken cancellationToken = default);
}
```

The Pro.Push.SignalR package owns the abstraction. Platform provides the implementation (`CachedAgentTenantResolver`) — not Pro, because the agent-tenant lookup is a Platform-domain concern (Platform owns the `agents` table).

### Hub method enforcement

```csharp
// PlatformHub.SubscribeToAgentPresenceAsync (rewritten):
public async Task SubscribeToAgentPresenceAsync(string agentId, CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(agentId)) return;

    var callerTenant = Context.User?.FindFirstValue("tid")
        ?? throw new HubException("Caller has no tenant claim.");

    var agentTenant = await _tenantResolver.GetTenantIdAsync(agentId, ct);
    if (agentTenant is null || agentTenant != callerTenant)
    {
        PlatformHubLog.CrossTenantSubscriptionDenied(_logger, callerTenant, agentId, agentTenant);
        await _audit.WriteAsync(new AuditEntry
        {
            Action = "hub.cross_tenant_subscription_denied",
            ActorTenantId = callerTenant,
            TargetId = agentId,
            Metadata = JsonSerializer.Serialize(new { agentTenant })
        });
        throw new HubException("Cross-tenant subscription not authorized.");
    }

    await Groups.AddToGroupAsync(Context.ConnectionId, $"presence:agent:{agentId}", ct);
}
```

### Resolver implementation (Platform side)

```csharp
public sealed class CachedAgentTenantResolver : IAgentTenantResolver
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private readonly NpgsqlDataSource _ds;
    private readonly IMemoryCache _cache;

    public CachedAgentTenantResolver(NpgsqlDataSource ds, IMemoryCache cache)
    {
        _ds = ds;
        _cache = cache;
    }

    public async Task<string?> GetTenantIdAsync(string agentId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue<string?>($"agent-tenant:{agentId}", out var cached))
            return cached;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        var tenantId = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT tenant_id FROM asterisk_platform.agents WHERE agent_id = @AgentId",
            new { AgentId = agentId });

        _cache.Set($"agent-tenant:{agentId}", tenantId, CacheDuration);
        return tenantId;
    }
}
```

### Lateral invalidation

When agent tenant membership changes (admin moves an agent between tenants — operationally rare), the change emits a Pro.Push event:

```csharp
public sealed record AgentTenantMembershipChangedEvent(string AgentId, string OldTenantId, string NewTenantId)
    : PushEvent;
```

A subscriber inside the Platform process listens for this event and clears the cache key:

```csharp
_pushBus.Subscribe<AgentTenantMembershipChangedEvent>(evt =>
{
    _cache.Remove($"agent-tenant:{evt.AgentId}");
});
```

This bounds the staleness window: changes propagate to all Platform replicas within the time it takes the Pro.Push backplane to fan out (≤ 1 second typically). For a 5-minute cache, the worst-case window where a moved supervisor can still subscribe to the old tenant's agent is `cache_remaining_ttl + propagation_delay` (typically < 1 second after rotation).

### Audit entry

`hub.cross_tenant_subscription_denied` audit entries land in the standard audit log with:

- `ActorTenantId` — caller's tenant (from JWT).
- `TargetId` — `agentId` they tried to subscribe to.
- `Metadata` — JSON containing the `agentTenant` (the tenant they should have subscribed via).

This produces a security signal that SOC operators / SIEM can alert on: a sustained pattern of denials by a single actor indicates either a misconfigured client or an attempted enumeration.

## Consequences

**Positivas:**
- Closes B.15 cross-tenant info leak. SOC 2 readiness narrative gains a real "cross-tenant boundary enforced at the hub" story.
- Cache amortizes DB load — typical supervisor opens a session, subscribes to 10-50 agents over a few minutes, then keeps the cached tenant for 5 min. ≤ 50 cache misses per supervisor session.
- Lateral invalidation handles the rare move-agent-between-tenants case correctly.
- Audit signal feeds into SIEM (R6 territory) as a security KPI.

**Negativas:**
- **5-minute staleness window** for a moved agent. A supervisor still in the cache after the move can subscribe to the old tenant's agent for up to 5 min. **Acceptable** because: (a) tenant moves are operationally rare; (b) the supervisor must already have valid JWT for the old tenant to even hit the hub method; (c) lateral invalidation reduces the window to well under 1 second in practice.
- **Adds DB query** on cache miss (mitigated by cache).
- **New abstraction in Pro.Push.SignalR** — small surface (1 interface, 1 method).

## Alternatives considered

- **(a) JWT claim trust-only:** rejected — token-staleness vector. Tenant membership can change post-token-issuance; a moved supervisor retains old-tenant subscription rights until token expiry.
- **(b) Database round-trip per call:** rejected — hot-path on connection-init bursts adds DB load + latency. Acceptable as fallback if the cache layer fails (cache miss → DB) but not as the only path.
- **(c) Cached resolver (chosen):** balances correctness (with lateral invalidation) and performance.
- **(d) Encode tenant in `agentId`** (e.g., `agent-{tenantId}-{shortId}`): rejected — brittle, breaks if `agentId` format ever changes; doesn't survive admin moving an agent between tenants.
- **(e) Keep the bug, mitigate at infrastructure level:** rejected — security-defense-in-depth requires the hub method itself to enforce; can't rely on operational firewall configuration alone.

## Migration guide (R5.2 P0.6 execution)

```diff
  // Pro.Push.SignalR/Hubs/PlatformHub.cs
+ private readonly IAgentTenantResolver _tenantResolver;
+ private readonly IAuditWriter _audit;
  // Inject in ctor

  public async Task SubscribeToAgentPresenceAsync(string agentId, CancellationToken ct = default)
  {
      if (string.IsNullOrWhiteSpace(agentId)) return;
+     var callerTenant = Context.User?.FindFirstValue("tid")
+         ?? throw new HubException("Caller has no tenant claim.");
+     var agentTenant = await _tenantResolver.GetTenantIdAsync(agentId, ct);
+     if (agentTenant is null || agentTenant != callerTenant)
+     {
+         await _audit.WriteAsync(new AuditEntry { ... });
+         throw new HubException("Cross-tenant subscription not authorized.");
+     }
      await Groups.AddToGroupAsync(Context.ConnectionId, $"presence:agent:{agentId}", ct);
  }

  // Platform Program.cs
+ services.AddSingleton<IAgentTenantResolver, CachedAgentTenantResolver>();
+ // Subscribe to AgentTenantMembershipChangedEvent for lateral invalidation
```

## References

- ADR-0002 (tenant stamping policy): `docs/decisions/0002-tenant-stamping-pipeline-end-to-end.md`
- ADR-0004 (per-package conventions): `docs/decisions/0004-tenant-stamping-execution-conventions.md`
- R5.2 spec §B.15: `docs/plans/active/2026-04-25-r5.2-security-admin-compliance.md`
- R5.2 execution plan P0.6: `docs/plans/active/2026-04-25-r5.2-execution-plan.md`
- Wave 2 audit Pro.Push.SignalR section (2026-04-25)

## Update — R5.4 (2026-04-26)

The R5.2 implementation registered `IAgentTenantResolver` as **optional** (no-op
fallback) for backwards compatibility. R5.4 closes this loop: registration is
now **required by default**. Consumers must call either:

- `services.WithAgentTenantResolver<TYourResolver>()` — recommended path
- `services.WithoutAgentTenantResolver()` — explicit opt-out with startup warning

The implicit no-op fallback is removed. Existing consumers that did not register
a resolver in R5.2/R5.3 will fail at startup with a clear error message
pointing to this ADR.

This change is breaking-but-safe: the opt-out path preserves legacy behavior,
giving consumers a one-line migration during the v1.13.x patch window.

The enforcement is implemented as an `IHostedService`
(`AgentTenantResolverEnforcement`) registered automatically by
`AddAsteriskProPushSignalR()`. It validates the registration at host startup
(before any client can connect to the hub), independent of the order in which
the resolver is registered relative to `AddAsteriskProPushSignalR()` itself.

R5.3 ADR-0007's `WithStrictMode()` opt-in remains relevant for the orthogonal
**connect-time** failure mode (per-connection check on the hub instance) — it
is not superseded by this update; the two work in tandem when both are
configured.

Platform impact: no Program.cs change required because Platform already
registers `CachedAgentTenantResolver` via `AddSingleton<IAgentTenantResolver,
CachedAgentTenantResolver>()` since R5.2 P0.6.
