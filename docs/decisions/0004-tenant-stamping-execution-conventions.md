# ADR-0004: Tenant stamping execution conventions per-package

- **Status:** Accepted (executed during R5.2 Phase 0, 2026-04-25)
- **Date:** 2026-04-25
- **Deciders:** Harold Reina
- **Related:**
  - ADR-0002 (`docs/decisions/0002-tenant-stamping-pipeline-end-to-end.md`) — policy-level parent
  - ADR-0005 (`docs/decisions/0005-cross-tenant-signalr-subscription-validation.md`) — cross-tenant subscription companion
  - R5.2 implementation spec: `docs/plans/active/2026-04-25-r5.2-security-admin-compliance.md`
  - R5.2 execution plan: `docs/plans/active/2026-04-25-r5.2-execution-plan.md`
  - Wave 2 audit findings (2026-04-25, in R5.2 spec §"Audit findings absorbed into R5.2 scope")

## Context

ADR-0002 (Accepted 2026-04-25) defines the **policy** for tenant stamping: every cross-tenant abstraction MUST inject `ITenantContext` and stamp `TenantId` on emitted artifacts; `tenantId == ""` is rejected; process-scope singletons are restricted to cluster/transport/stateless utilities.

The Wave 2 audit (run 2026-04-25, R5.2 brainstorm session) discovered that **four Pro packages** had distinct execution patterns of non-compliance against ADR-0002:

| Package | Violation pattern | Severity |
|---|---|---|
| Pro.EventStore | `EventStoreSubscriber.cs:117,122` falls back to `tenantId = ""` if session not found OR `session.TenantId` null. Same shape as the R5.1 `LiveQueueSnapshotWriter` bug that motivated ADR-0002. | P0 (silent multi-tenant data corruption) |
| Pro.CallAnalytics | `CallAnalyticsEngine.cs:183` creates fallback `CompletedSessionRow { TenantId = serverId }` when CDR missing. Cross-tenant data leak in multi-server deploys. | P1 |
| Pro.Push.SignalR | `PlatformHub.SubscribeToAgentPresenceAsync(string agentId)` accepts any `agentId` without validating ownership. Bridges fall back to `BridgeOptions.DefaultTenantId="default"` because hosted services lack request scope. | P0 (security) + P2 (data) |
| Pro.Analytics | `AddAsteriskAnalytics()` registered as process-scope singleton with `DefaultTenantId=""` — original R5.1 limitation #1, baseline of the policy. | P0 (silent multi-tenant data corruption) |

ADR-0002 is the policy. **This ADR documents the per-package execution conventions** that materialize the policy across the four packages — a single decision point so future package additions follow the same pattern without re-arguing per-package.

## Decision

Adopt the following per-package conventions. Each is non-negotiable when the policy applies; when a package has none of these patterns (e.g., stateless transports), the policy is moot.

### Pro.Analytics

**Pattern:** opt-in single-tenant mode via builder.

```csharp
// Single-tenant deploys (today's baseline) — explicit opt-in
services.AddAsteriskAnalytics()
    .WithSingleTenantMode("default");

// Multi-tenant deploys — inject ITenantContext (no opt-in needed)
services.AddAsteriskAnalytics();  // requires registered ITenantContext in DI
```

- The implicit `DefaultTenantId = ""` fallback is **removed**. Calls to `AddAsteriskAnalytics()` without `WithSingleTenantMode(...)` and without registered `ITenantContext` fail-fast at startup.
- `LiveQueueSnapshotWriter` reads `event.Metadata.TenantId`; if empty, log + emit metric `analytics.events.rejected{reason="missing_tenant"}`; do NOT persist.

### Pro.EventStore

**Pattern:** events MUST include `TenantId` in payload; subscriber rejects empty.

```csharp
// EventStoreSubscriber.HandleEventAsync (rewritten):
var session = _sessionManager.GetById(evt.SessionId);
if (session is null)
{
    EventStoreSubscriberLog.SkippedMissingSession(_logger, evt.SessionId);
    _metrics.EventsSkipped.Add(1, new KeyValuePair<string, object?>("reason", "missing_session"));
    return;
}
if (string.IsNullOrEmpty(session.TenantId))
{
    EventStoreSubscriberLog.SkippedEmptyTenant(_logger, evt.SessionId);
    _metrics.EventsSkipped.Add(1, new KeyValuePair<string, object?>("reason", "missing_tenant"));
    return;
}
var tenantId = session.TenantId;
```

- `SessionEventRow.TenantId` and `CompletedSessionRow.TenantId` are `required string ... { get; init; }` (no default empty). Forces callers to provide.
- New counter `eventstore.events.skipped` with `reason` tag tracks rejections.

### Pro.CallAnalytics

**Pattern:** `CallAnalyticsEngine` injects `ITenantContext` (or accepts per-event tenant from CDR); reject when CDR missing instead of fabricating tenant.

```csharp
// CallAnalyticsEngine.ProcessCallAsync (rewritten):
var cdr = await _completedSessionStore.GetAsync(endedEvt.TenantId, sessionId, ct);
if (cdr is null)
{
    CallAnalyticsEngineLog.SkippedMissingCdr(_logger, sessionId, endedEvt.TenantId);
    _metrics.EventsSkipped.Add(1, new KeyValuePair<string, object?>("reason", "missing_cdr"));
    return;
}
// proceed using cdr.TenantId — no serverId fallback
```

- `ICompletedSessionStore.GetAsync(tenantId, sessionId, ct)` signature locked: tenantId is the FIRST parameter, never `serverId`.
- Removes the `CompletedSessionRow { TenantId = serverId }` fabrication at line 183.

### Pro.Push.SignalR

**Pattern:** hub methods that subscribe to other tenants' state validate ownership via `IAgentTenantResolver` (per ADR-0005); bridges require explicit `WithDefaultTenantId(string)` builder call.

```csharp
// PlatformHub.SubscribeToAgentPresenceAsync (rewritten):
var callerTenant = Context.User?.FindFirstValue("tid")
    ?? throw new HubException("Caller has no tenant claim.");
var agentTenant = await _tenantResolver.GetTenantIdAsync(agentId, ct);
if (agentTenant is null || agentTenant != callerTenant)
{
    await _audit.WriteAsync(new AuditEntry { Action = "hub.cross_tenant_subscription_denied", ... });
    throw new HubException("Cross-tenant subscription not authorized.");
}
await Groups.AddToGroupAsync(Context.ConnectionId, $"presence:agent:{agentId}", ct);

// Bridges builder requires explicit tenant fallback:
services.AddProPushBridges()
    .WithClusterEventBridge()
    .WithConversationBridge(opt => /* ... */)
    .WithAgentBridge()
    .WithDefaultTenantId("default");  // throws InvalidOperationException at startup if omitted in multi-tenant deploys
```

- The implicit `BridgeOptions.DefaultTenantId = "default"` fallback is **removed** from the `BridgeOptions` defaults.
- Bridges throw `InvalidOperationException` at startup if multi-tenant deploy detected (more than one tenant in `ITenantStore`) AND `DefaultTenantId` not set explicitly.

### Observability tag patterns (cross-cutting all 4 packages)

- **Activity tags:** `AsteriskSemanticConventions.Tenant.Id` is **mandatory** on every span emitted by Pro.AgentAssist, Pro.EventStore, Pro.CallAnalytics, Pro.Push.SignalR (and bridges) when the operation traverses tenant context.
- **Meter tag dimension:** `tenant_id` is **required** on Counter/Histogram emissions across the 4 packages where cardinality permits. Pro packages have 3-100 tenants typical → cardinality permits. Where the cardinality is unbounded (e.g., per-agent counters), the package documents the choice explicitly in its `Diagnostics/README.md`.
- **No reflection** to look up the tenant — always pass it explicitly through method parameters or capture it from an injected `ITenantContext`. Static dispatch only.

### Schema CHECK constraints

- All Postgres tables that store tenant-scoped rows MUST have `CHECK (tenant_id <> '')` constraint, enforced at schema level. Tables without `tenant_id` column (cluster-level state, transport state) are exempt.
- New migrations follow the pattern in R5.2 P0.2: `DELETE FROM <table> WHERE tenant_id = ''` before `ALTER TABLE ... ADD CONSTRAINT chk_tenant_id_nonempty CHECK (tenant_id <> '')`.

### Subscriber/HostedService registration

When a `BackgroundService` / `IHostedService` consumes events that flow across tenants:

- It receives `ITenantContext` only when the event itself carries `TenantId` (capture at emit time, not at consume time).
- It does **not** mutate `TenantContext.Current` — that pattern is for request scope (HTTP middleware), not async event flows.
- It logs+skips events with missing tenant (per package patterns above).

## Consequences

**Positivas:**
- Closes silent multi-tenant data corruption across 4 packages atomically (R5.2 Phase 0 mega-track).
- Single decision point — future Pro package additions (e.g., Pro.CallbackQueue if v1.9.x lands) inherit the conventions without per-package ADR.
- Database CHECK constraints make the bug detectable at INSERT time (rejected) rather than at query-result time (silent overlap).
- Aligns with `AsteriskSemanticConventions.Tenant.Id` adoption already in flight (7 Pro packages, 23 call-sites since Pro v1.10.0-pro).
- Brings R5.4 multi-tenant load-test scenario (S5.1 Track A) into a meaningful state — without these fixes, multi-tenant load tests can't separate clean signal.

**Negativas:**
- **Pro 1.13.0-pro is breaking-fix per CHANGELOG annotation.** Callers using `AddAsteriskAnalytics(opt => opt.DefaultTenantId = "")` get a clear startup error pointing to migration. Callers using `BridgeOptions.DefaultTenantId="default"` implicit fallback get startup error. Both are bug-fix-not-API-change per ADR-0002 §"Bug-fix-not-API-change exemption" — prior behavior was data-corrupting.
- **Existing operational deploys must migrate** — single-tenant deploys add `.WithSingleTenantMode("default")` to `AddAsteriskAnalytics()` registration; bridges callers add `.WithDefaultTenantId("default")` to bridge builder.
- **R5.2 dev cost** ~5-7 días for the Phase 0 mega-track (P0.3-P0.7). Within R5.2 envelope.

## Alternatives considered

- **Per-package ADRs separately:** rejected — too fragmented; users navigating the 4 ADRs would lose the pattern. Single ADR captures the meta-decision once.
- **Defer to inline code documentation:** rejected — needs decision-record formalism for future onboarding. Devs in 6 months ask "why does `AddAsteriskAnalytics` require `WithSingleTenantMode`?" and an ADR provides the answer with full context.
- **Refactor every Pro process-scope singleton to scoped:** rejected — too disruptive in R5.2 envelope. The audit-driven scope (4 packages with confirmed violations) is the right size; future audit sweeps can add more if needed.

## Migration guide (executed during R5.2 Phase 0)

For each affected callsite in Platform `Program.cs`:

```diff
  // Pro.Analytics
- services.AddAsteriskAnalytics(opt => opt.DefaultTenantId = "");
+ services.AddAsteriskAnalytics()
+     .WithSingleTenantMode("default");

  // Pro.Push.SignalR bridges
- services.AddProPushBridges()
-     .WithClusterEventBridge()
-     .WithConversationBridge(opt => { /* ... */ })
-     .WithAgentBridge();
+ services.AddProPushBridges()
+     .WithClusterEventBridge()
+     .WithConversationBridge(opt => { /* ... */ })
+     .WithAgentBridge()
+     .WithDefaultTenantId("default");
```

For each consumer of `ICompletedSessionStore` (Pro.CallAnalytics is the canonical case):

```diff
- await _completedSessionStore.GetAsync(serverId, sessionId, ct);
+ await _completedSessionStore.GetAsync(endedEvt.TenantId, sessionId, ct);
```

No changes required for callers using `ITenantContext` correctly.

## References

- ADR-0002 (parent policy): `docs/decisions/0002-tenant-stamping-pipeline-end-to-end.md`
- ADR-0005 (cross-tenant subscription validation): `docs/decisions/0005-cross-tenant-signalr-subscription-validation.md`
- R5.2 spec §"Audit findings absorbed into R5.2 scope": `docs/plans/active/2026-04-25-r5.2-security-admin-compliance.md`
- R5.2 execution plan §Phase 0 (P0.3-P0.7): `docs/plans/active/2026-04-25-r5.2-execution-plan.md`
- Pro v1.10.0-pro `AsteriskSemanticConventions.Tenant` adoption (precedent): `Asterisk.Sdk.Pro/CHANGELOG.md`
- SDK `Asterisk.Sdk.Cluster.Primitives.AsteriskSemanticConventions` (v1.15.0): canonical attribute names
