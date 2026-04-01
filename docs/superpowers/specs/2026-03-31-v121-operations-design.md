# v1.2.1 "Operations" Design Spec

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Deliver runtime cluster management, tenant impersonation, and AOT hardening for production multi-instance deployments.

**Architecture:** 5 sub-projects across 3 repos (SDK Pro, Platform, Platform.Web). PostgresClusterTransport enables multi-instance state sharing; thin REST layer exposes ClusterManager SDK capabilities; Shadow JWT provides audited impersonation; anonymous DTO replacement ensures full AOT compatibility.

**Tech Stack:** .NET 10 Native AOT, PostgreSQL (LISTEN/NOTIFY), Dapper, TanStack Query 5, React 19, Recharts (summary only), Zustand, Playwright E2E.

---

## Sub-projects Overview

| # | Name | Repo | Scope |
|---|------|------|-------|
| A | PostgresClusterTransport | SDK Pro | Persistent transport for multi-instance cluster state |
| B | Server Management API | SDK Pro + Platform | 6 new endpoints + UpdateNodeAsync SDK method |
| C | Impersonation (Shadow JWT) | Platform + Platform.Web | Audited tenant impersonation with short-lived JWT |
| D | Cluster UI | Platform.Web | Dedicated cluster management page |
| E | Anonymous DTO Hardening | Platform | Replace 72 `new {}` with typed sealed records |

**Dependencies:** A → B → D (sequential), C independent, E independent.

**Recommended execution order:** E first (mechanical, enables `ErrorResponse` for all other sub-projects), then A → B in SDK Pro, then C + D in parallel.

---

## Sub-project A: PostgresClusterTransport

### Goal

Replace `InMemoryClusterTransport` with PostgreSQL-backed persistence so multiple Platform API instances share cluster state (nodes, drains, sessions, locks, heartbeats).

### New File

`Asterisk.Sdk.Pro.Cluster/Transport/PostgresClusterTransport.cs`

### Schema

```sql
-- cluster_nodes: registered Asterisk nodes
CREATE TABLE cluster_nodes (
    node_id          TEXT PRIMARY KEY,
    ami_hostname     TEXT NOT NULL,
    ami_port         INT NOT NULL DEFAULT 5038,
    ami_username     TEXT NOT NULL,
    ami_password     TEXT NOT NULL,
    state            TEXT NOT NULL DEFAULT 'Unknown',
    owner_instance   TEXT,
    generation       BIGINT NOT NULL DEFAULT 0,
    weight           DOUBLE PRECISION NOT NULL DEFAULT 1.0,
    priority_tier    INT NOT NULL DEFAULT 0,
    max_capacity     INT NOT NULL DEFAULT 500,
    tags             JSONB,
    asterisk_version TEXT,
    startup_time     TIMESTAMPTZ
);

-- cluster_instances: Platform API instance heartbeats
CREATE TABLE cluster_instances (
    instance_id      TEXT PRIMARY KEY,
    owned_node_ids   JSONB NOT NULL DEFAULT '[]',
    total_channels   INT NOT NULL DEFAULT 0,
    total_agents     INT NOT NULL DEFAULT 0,
    last_seen        TIMESTAMPTZ NOT NULL,
    expires_at       TIMESTAMPTZ NOT NULL
);

-- cluster_session_snapshots: active call snapshots for failover
CREATE TABLE cluster_session_snapshots (
    server_id        TEXT NOT NULL,
    linked_id        TEXT NOT NULL,
    session_id       TEXT NOT NULL,
    state            TEXT NOT NULL,
    direction        TEXT NOT NULL,
    created_at       TIMESTAMPTZ NOT NULL,
    queue_name       TEXT,
    agent_id         TEXT,
    bridge_id        TEXT,
    hold_time        INTERVAL,
    metadata         JSONB,
    PRIMARY KEY (server_id, linked_id)
);

-- cluster_drain_states: active drain tracking
CREATE TABLE cluster_drain_states (
    node_id              TEXT PRIMARY KEY,
    state                TEXT NOT NULL,
    started_at           TIMESTAMPTZ NOT NULL,
    deadline             TIMESTAMPTZ NOT NULL,
    initial_call_count   INT NOT NULL DEFAULT 0,
    remaining_call_count INT NOT NULL DEFAULT 0,
    naturally_completed  INT NOT NULL DEFAULT 0,
    force_disconnected   INT NOT NULL DEFAULT 0
);

-- cluster_locks: distributed advisory locks
CREATE TABLE cluster_locks (
    resource         TEXT PRIMARY KEY,
    owner            TEXT NOT NULL,
    expires_at       TIMESTAMPTZ NOT NULL
);

-- cluster_generations: optimistic concurrency counters
CREATE TABLE cluster_generations (
    node_id          TEXT PRIMARY KEY,
    generation       BIGINT NOT NULL DEFAULT 0
);
```

### Pub/Sub

PostgreSQL `LISTEN/NOTIFY` on channel `cluster_events`. Payload: JSON-serialized `ClusterEvent`. Background listener thread with `NpgsqlConnection` in async wait mode.

### DI Registration

```csharp
public static IServiceCollection UsePostgresClusterTransport(
    this IServiceCollection services, string connectionString)
```

Replaces the default `TryAddSingleton<ClusterTransportBase, InMemoryClusterTransport>` with `PostgresClusterTransport`.

### Schema Management

`EnsureSchemaAsync()` pattern with `_schema_cluster` tracking table, same as other Pro packages.

### Abstract Methods Implemented

All 18 abstract methods from `ClusterTransportBase`:
- Node registry: Register, Unregister, GetNodes, UpdateNodeState
- Pub/Sub: Publish, Subscribe
- Locking: TryAcquireLock, ReleaseLock
- Heartbeat: Heartbeat, GetLiveInstances
- Sessions: Save/Get/GetForServer/Remove SessionSnapshot
- Generations: IncrementGeneration
- Drains: Save/Get/Remove DrainState

### Tests

~20 tests covering CRUD, lock contention, pub/sub delivery, heartbeat TTL expiry, drain state persistence.

---

## Sub-project B: Server Management API

### Goal

Expose runtime cluster node CRUD and drain lifecycle operations via PlatformAdminOnly REST endpoints.

### SDK Change: UpdateNodeAsync

**File:** `Asterisk.Sdk.Pro.Cluster/ClusterManager.cs`

New public method:
```csharp
public async ValueTask UpdateNodeAsync(
    string nodeId,
    double? weight = null,
    int? priorityTier = null,
    int? maxCapacity = null,
    IReadOnlyDictionary<string, string>? tags = null,
    CancellationToken cancellationToken = default)
```

Updates in-memory `ClusterNode` properties + persists via transport. No AMI reconnection needed — routing parameters only.

**File:** `Asterisk.Sdk.Pro.Cluster/Transport/ClusterTransportBase.cs`

New abstract method:
```csharp
public abstract ValueTask UpdateNodeAsync(
    string nodeId, NodeUpdate update, CancellationToken ct);
```

New model:
```csharp
public sealed record NodeUpdate(
    double? Weight, int? PriorityTier, int? MaxCapacity,
    IReadOnlyDictionary<string, string>? Tags);
```

Implemented in both `InMemoryClusterTransport` and `PostgresClusterTransport`.

### New Endpoints

**File:** `ManagementClusterEndpoints.cs` (extend existing)

| Method | Route | Action | SDK Call |
|--------|-------|--------|---------|
| POST | `/api/management/cluster/nodes` | Register new node | `ClusterManager.AddNodeAsync()` |
| PUT | `/api/management/cluster/nodes/{nodeId}` | Update weight/tier/capacity/tags | `ClusterManager.UpdateNodeAsync()` |
| DELETE | `/api/management/cluster/nodes/{nodeId}` | Unregister node | `ClusterManager.RemoveNodeAsync()` |
| DELETE | `/api/management/cluster/nodes/{nodeId}/drain` | Cancel active drain | `DrainManager.CancelDrainAsync()` |
| POST | `/api/management/cluster/nodes/{nodeId}/force-drain` | Force immediate drain | `DrainManager.ForceDrainAsync()` |
| GET | `/api/management/cluster/instances` | List live Platform instances | `ClusterStatus.LiveInstances` |

Existing endpoints (unchanged): GET status, GET nodes, GET node/{id}, POST drain.

**Total: 10 endpoints** (4 existing + 6 new).

### New DTOs

```csharp
internal sealed record CreateNodeRequest(
    string NodeId,
    string AmiHostname,
    int AmiPort,
    string AmiUsername,
    string AmiPassword,
    double Weight = 1.0,
    int PriorityTier = 0,
    int MaxCapacity = 500,
    Dictionary<string, string>? Tags = null);

internal sealed record UpdateNodeRequest(
    double? Weight,
    int? PriorityTier,
    int? MaxCapacity,
    Dictionary<string, string>? Tags);

internal sealed record MgmtInstanceDto(
    string InstanceId,
    DateTimeOffset LastSeen,
    IReadOnlyList<string> OwnedNodeIds,
    int TotalChannels,
    int TotalAgents);
```

### Updated DTOs

- `MgmtClusterStatusDto`: add `IReadOnlyList<MgmtInstanceDto> Instances` field
- `MgmtDrainStatusDto`: add `TimeSpan? EstimatedTimeToZero` field

### Validations

- POST node: 409 Conflict if nodeId already exists
- PUT node: 404 if node not found
- DELETE node: 400 if state is Healthy or Draining (must drain first)
- DELETE drain: 404 if no active drain for nodeId
- POST force-drain: 404 if no active drain for nodeId

### ApiJsonContext Registration

Register all new DTOs: `CreateNodeRequest`, `UpdateNodeRequest`, `MgmtInstanceDto`, `List<MgmtInstanceDto>`.

### Tests

~15 tests covering CRUD operations, validation rules, drain lifecycle.

---

## Sub-project C: Impersonation (Shadow JWT)

### Goal

Allow Platform Admins to operate in the context of a child tenant with a short-lived shadow JWT, full audit trail, and explicit UI indication.

### Permission

`platform:tenant:impersonate` — already defined in `PermissionSeeder.cs` and assigned to `platform_admin` role template.

### Backend: Endpoints

**New file:** `ManagementImpersonationEndpoints.cs`

| Method | Route | Action |
|--------|-------|--------|
| POST | `/api/management/impersonate` | Start impersonation → shadow JWT |
| DELETE | `/api/management/impersonate` | End impersonation (audit log) |

**Request:**
```csharp
internal sealed record ImpersonateRequest(string TargetTenantId);
```

**Response:**
```csharp
internal sealed record ImpersonateResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string TargetTenantId,
    string TargetTenantName);
```

### POST /impersonate Flow

1. Verify caller has `platform:tenant:impersonate` permission
2. Verify `TargetTenantId` exists via `ITenantStore.GetAsync()`
3. Verify target is a child of caller's tenant (Platform → Partner → Customer hierarchy)
4. Verify target tenant status is `Active`
5. Resolve target tenant's available permissions: use the `system_admin` role template permissions (52 non-platform permissions). The impersonator doesn't have role assignments in the target tenant, so we use the ceiling of what a tenant-scoped Admin could do — all permissions except `platform:*` ones
6. Generate shadow JWT via `JwtTokenService.GenerateImpersonationToken()`:
   - `sub` = admin user ID (unchanged)
   - `tid` = target tenant ID
   - `impersonator_id` = admin user ID
   - `impersonator_tenant` = admin's original tenant ID
   - `impersonation` = `"true"`
   - `role` = `"Admin"` (ceiling within target tenant)
   - `permissions` = target-scoped permissions (no `platform:*`)
   - TTL: 30 minutes
   - No refresh token
7. Log `AuthEvent`: type=`impersonation_started`, details=`{targetTenantId, impersonatorId, impersonatorTenant}`
8. Return `ImpersonateResponse`

### DELETE /impersonate Flow

1. Verify JWT has `impersonation=true` claim
2. Log `AuthEvent`: type=`impersonation_ended`, details=`{targetTenantId, duration}`
3. Return 204 No Content

### Backend: JwtTokenService Change

**File:** `JwtTokenService.cs`

New method:
```csharp
public string GenerateImpersonationToken(
    User admin,
    string targetTenantId,
    IReadOnlySet<string> targetPermissions)
```

Same as `GenerateAccessToken` but:
- `tid` = targetTenantId (overrides admin's tenant)
- Adds `impersonator_id`, `impersonator_tenant`, `impersonation` claims
- TTL: 30 minutes (vs normal 15 minutes)

### Backend: Middleware Restrictions

**File:** `TenantResolutionMiddleware.cs`

When JWT has `impersonation=true` claim, block these paths with 403 Forbidden:
- `DELETE /api/management/tenants/*` — no deleting tenants while impersonating
- `POST /api/management/impersonate` — no recursive impersonation
- `PUT /api/management/system/*` — no changing system settings
- `POST /api/setup` — no setup wizard

Response: `{ "error": "Operation not allowed during impersonation" }` (uses `ErrorResponse` DTO from Sub-project E).

### Backend: Auth Event Types

**File:** `AuthEvent.cs` or constants file

```csharp
public const string ImpersonationStarted = "impersonation_started";
public const string ImpersonationEnded = "impersonation_ended";
```

### Frontend: Auth Store Changes

**File:** `auth-store.ts`

New state:
```typescript
impersonation: {
  active: boolean;
  targetTenantId: string;
  targetTenantName: string;
  originalToken: string;
  originalTenantId: string;
} | null;

startImpersonation(response: ImpersonateResponse, originalToken: string): void;
endImpersonation(): void;
```

`startImpersonation`: saves original token, replaces `accessToken` with shadow token, sets `tenantId` = target.

`endImpersonation`: restores original token, calls `DELETE /api/management/impersonate`, clears impersonation state.

### Frontend: Impersonation Banner

**New file:** `ImpersonationBanner.tsx`

Fixed banner at top of app layout (above sidebar):
```
⚠️ Operating as [Tenant Name] — [28:45 remaining] — [End Impersonation]
```
- Amber/warning color scheme
- Countdown timer from TTL
- "End Impersonation" button calls `endImpersonation()`
- Only renders when `impersonation?.active === true`

### Frontend: Impersonation Hook

**New file:** `use-impersonation.ts`

```typescript
useImpersonate()     // POST /api/management/impersonate
useEndImpersonate()  // DELETE /api/management/impersonate
```

### Frontend: Tenant Management Integration

Add "Impersonate" button in tenant list rows (ManagementTenantEndpoints page), visible only for Active tenants and users with `platform:tenant:impersonate` permission.

### ApiJsonContext Registration

Register: `ImpersonateRequest`, `ImpersonateResponse`.

### Tests

~12 backend tests: successful impersonation, hierarchy validation, privilege ceiling, blocked operations, audit events, expired token.

---

## Sub-project D: Cluster UI

### Goal

Dedicated `/admin/cluster` page consolidating cluster info from system-page and diagnostics-page. Table-based layout with CRUD, drain management, and instance visibility.

### New Page: `cluster-page.tsx`

**Route:** `/admin/cluster`
**Permission:** `platform:cluster:manage`

**Layout:**

```
┌─ PageHeader: "Cluster Management" ─── [Add Node] ─────────┐
│                                                             │
├─ Summary Cards (grid-cols-4) ──────────────────────────────┤
│ Nodes: 5/5 healthy │ Channels: 342/2500 │ Agents: 28 │ Instances: 2 │
│                                                             │
├─ DataTable: Nodes ─────────────────────────────────────────┤
│ Node ID │ State │ Channels │ Weight │ Tier │ Owner │ Actions│
│ ast-1   │ 🟢    │ 142/500  │ 1.0    │ 0    │ api-1 │ ⋮     │
│ ast-2   │ 🟡    │ 200/500  │ 2.0    │ 0    │ api-2 │ ⋮     │
│ ast-3   │ 🔴    │ 0/500    │ 1.0    │ 1    │ —     │ ⋮     │
│                                                             │
├─ Active Drains (collapsible, amber bg) ────────────────────┤
│ ast-4 │ Draining │ 12 remaining │ ETA ~3m │ [Cancel] [Force] │
│                                                             │
├─ Platform Instances (collapsible) ─────────────────────────┤
│ api-1 │ Last seen: 3s ago │ Nodes: ast-1, ast-3 │ Ch: 142 │
│ api-2 │ Last seen: 5s ago │ Nodes: ast-2, ast-4 │ Ch: 200 │
└─────────────────────────────────────────────────────────────┘
```

### State Badges

| State | Color |
|-------|-------|
| Healthy | green (default) |
| Degraded | yellow (warning) |
| Unhealthy | red (destructive) |
| Draining | amber |
| Offline | gray (secondary) |
| Unknown | gray (secondary) |

### Actions Per Node State

| State | Available Actions |
|-------|------------------|
| Healthy | Edit, Drain |
| Degraded | Edit, Drain |
| Draining | Cancel Drain, Force Drain |
| Offline | Edit, Remove |
| Unhealthy | Edit, Drain, Remove |

- **Edit** → Sheet with form (weight, priorityTier, maxCapacity, tags)
- **Drain** → Dialog with gracePeriodSeconds input
- **Cancel Drain** → ConfirmDialog
- **Force Drain** → ConfirmDeleteDialog (3s countdown)
- **Remove** → ConfirmDeleteDialog (3s countdown)

### Add Node

Sheet with form:
- NodeId (text, required)
- AMI Hostname (text, required)
- AMI Port (number, default 5038)
- AMI Username (text, required)
- AMI Password (password, required)
- Weight (number, default 1.0)
- Priority Tier (number, default 0)
- Max Capacity (number, default 500)

### Hooks: `use-cluster.ts` Rewrite

**Fix path mismatch:** `/api/admin/cluster/` → `/api/management/cluster/`

```typescript
// Queries (all refetch every 10s)
useClusterStatus()       // GET /api/management/cluster/status
useClusterNodes()        // GET /api/management/cluster/nodes
useClusterInstances()    // GET /api/management/cluster/instances

// Mutations (all invalidate cluster-status + cluster-nodes)
useCreateNode()          // POST /api/management/cluster/nodes
useUpdateNode()          // PUT /api/management/cluster/nodes/{nodeId}
useDeleteNode()          // DELETE /api/management/cluster/nodes/{nodeId}
useDrainNode()           // POST /api/management/cluster/nodes/{nodeId}/drain
useCancelDrain()         // DELETE /api/management/cluster/nodes/{nodeId}/drain
useForceDrain()          // POST /api/management/cluster/nodes/{nodeId}/force-drain
```

### Consolidation

- **diagnostics-page.tsx**: Remove Cluster Nodes table and Active Drains section. Keep Platform Info and License cards only.
- **system-page.tsx**: Remove node cards and drain buttons. Keep system settings only.

### Sidebar

Add "Cluster" item in System group (between Diagnostics and Tenants). Icon: `Network` from lucide-react.

### data-testid Conventions

| Element | testid |
|---------|--------|
| Summary cards | `cluster-summary-nodes`, `cluster-summary-channels`, `cluster-summary-agents`, `cluster-summary-instances` |
| Node table | `cluster-nodes-table` |
| Add button | `cluster-add-node-btn` |
| Node actions | `cluster-node-{nodeId}-actions` |
| Drain section | `cluster-active-drains` |
| Instance section | `cluster-instances` |
| Add node sheet | `cluster-add-node-sheet` |
| Edit node sheet | `cluster-edit-node-sheet` |

### Auto-refresh

10 seconds (operational page, more frequent than diagnostics 15s).

---

## Sub-project E: Anonymous DTO Hardening

### Goal

Replace all 72 instances of `new { }` in endpoint files with typed sealed records registered in `ApiJsonContext`, ensuring full Native AOT compatibility.

### Pattern

**Error responses (~55 instances):**
```csharp
// Before:
return Results.BadRequest(new { error = "Tenant not found" });

// After:
return Results.BadRequest(new ErrorResponse("Tenant not found"));
```

Single shared DTO:
```csharp
internal sealed record ErrorResponse(string Error);
```

**Structured responses (~17 instances):**
Each gets its own sealed record defined at the bottom of the endpoint file (same pattern as existing `MgmtClusterStatusDto`, `MgmtTenantDto`).

### Files and New DTOs

| File | Instances | New DTOs |
|------|-----------|----------|
| AuthEndpoints.cs | 22 | `ErrorResponse` (shared), `MfaChallengeResponse`, `LoginStatusResponse` |
| ManagementTenantEndpoints.cs | 8 | `ErrorResponse` |
| OidcEndpoints.cs | 8 | `ErrorResponse`, `OidcDiscoveryResponse`, `OidcCallbackResponse` |
| RbacEndpoints.cs | 5 | `ErrorResponse` |
| ManagementBillingEndpoints.cs | 4 | `ErrorResponse` |
| ManagementSystemEndpoints.cs | 5 | `SystemInfoDto`, `LicenseInfoDto`, `SystemSettingsDto` |
| ChannelConfigEndpoints.cs | 3 | `ErrorResponse`, `ChannelStatusDto` |
| MediaEndpoints.cs | 3 | `ErrorResponse` |
| AnalyticsEndpoints.cs | 2 | `ErrorResponse` |
| ConversationEndpoints.cs | 2 | `ErrorResponse` |
| SetupEndpoints.cs | 2 | `ErrorResponse`, `SetupStatusDto` |
| WebhookEndpoints.cs | 2 | `ErrorResponse` |
| AuthAdminEndpoints.cs | 1 | `ErrorResponse` |
| SupervisorEndpoints.cs | 1 | `ErrorResponse` |

**Total new DTOs:** ~10 sealed records (1 shared `ErrorResponse` + ~9 specific response DTOs).

### ErrorResponse Location

`Endpoints/Shared/ErrorResponse.cs` — internal sealed record, used across all 14 files.

### ApiJsonContext Registration

Register all new DTOs in `ApiJsonContext.cs`:
```csharp
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(SystemInfoDto))]
[JsonSerializable(typeof(LicenseInfoDto))]
[JsonSerializable(typeof(SystemSettingsDto))]
[JsonSerializable(typeof(MfaChallengeResponse))]
[JsonSerializable(typeof(OidcDiscoveryResponse))]
[JsonSerializable(typeof(OidcCallbackResponse))]
[JsonSerializable(typeof(ChannelStatusDto))]
[JsonSerializable(typeof(SetupStatusDto))]
```

### Verification

After refactor: `grep -r "new {" src/Asterisk.Platform.Api/Endpoints/` returns **0 results** (excluding legitimate object initializers like `new Dictionary<string,string> { ... }`).

---

## Roadmap Update

### v1.2.1 "Operations" — This Release
- Sub-projects A through E as described above

### v1.3.0 "Compliance & Scale" — Next
- Grafana cluster dashboard (JSON dashboard file, linked from admin app)
- Cluster drain history persistence (SDK transport extension)
- Health timeline charts (Prometheus integration)
- License key validation (RSA activation, enforcement modes)
- GDPR compliance reporting
- SAML SSO

---

## Cross-Cutting Concerns

### NuGet Rebuild Cycle (Sub-projects A + B)

SDK Pro changes require:
1. Implement in `Asterisk.Sdk.Pro.Cluster`
2. `dotnet pack -c Release -o /media/Data/Source/IPcom/local-nuget-feed/`
3. `rm -rf ~/.nuget/packages/asterisk.sdk.pro.cluster*/`
4. `dotnet restore` in Platform

### Test Coverage Targets

| Sub-project | New Tests | Total After |
|-------------|-----------|-------------|
| A (Transport) | ~20 | ~890 (SDK Pro) |
| B (API) | ~15 | ~1,177 (Platform) |
| C (Impersonation) | ~12 | ~1,189 (Platform) |
| D (Cluster UI) | ~10 E2E | ~100 E2E (Platform.Web) |
| E (DTOs) | 0 (refactor) | unchanged |
| **Total** | **~57** | |

### Files Modified Summary

**SDK Pro (Asterisk.Sdk.Pro.Cluster):**
- New: `PostgresClusterTransport.cs`, `NodeUpdate.cs`
- Modified: `ClusterManager.cs`, `ClusterTransportBase.cs`, `InMemoryClusterTransport.cs`, DI extension

**Platform:**
- New: `ManagementImpersonationEndpoints.cs`, `Endpoints/Shared/ErrorResponse.cs`
- Modified: `ManagementClusterEndpoints.cs`, `JwtTokenService.cs`, `TenantResolutionMiddleware.cs`, `ApiJsonContext.cs`, 14 endpoint files (DTO hardening)

**Platform.Web:**
- New: `cluster-page.tsx`, `ImpersonationBanner.tsx`, `use-impersonation.ts`
- Modified: `use-cluster.ts` (rewrite), `auth-store.ts`, `sidebar.tsx`, `router.tsx`, `diagnostics-page.tsx`, `system-page.tsx`

### Execution Order

1. **E** (DTO Hardening) — mechanical, no dependencies, creates `ErrorResponse` used by C
2. **A** (PostgresClusterTransport) — SDK Pro, prerequisite for B
3. **B** (Server Management API) — SDK Pro + Platform, prerequisite for D
4. **C** (Impersonation) — Platform + Web, can start after E completes
5. **D** (Cluster UI) — Platform.Web, requires B endpoints to be live

C and D can run in parallel once their dependencies are met.
