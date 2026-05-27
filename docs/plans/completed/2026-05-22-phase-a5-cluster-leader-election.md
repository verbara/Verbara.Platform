# Phase A.5 — Pro.Cluster leader election + Realtime multi-pod scaling

> **Track:** ADR-0022 closure (last residual item)
> **Authored:** 2026-05-22, post v2.4.1 24h soak PASS
> **Estimated effort:** ~1.5 days actual work (1 dev session + smoke-test day)
> **Repos touched:** `Verbara.Sdk` (1 file), `Verbara.Sdk.Pro` (cluster + migration + DI), `Verbara.Platform` (Realtime + Helm chart)
> **Releases produced:** SDK 2.2.1, Pro 2.5.1-pro, Platform v2.4.2 (4 re-signed ghcr.io images)

## 1. Goal

Unblock multi-pod horizontal scaling of `Verbara.Platform.Realtime` by adding leader election to `Verbara.Sdk.Pro.Cluster` and leader-gating the `PushToHubRelay` so that only one pod publishes cross-pod events to the Redis backplane (eliminating duplicate SignalR delivery).

Once delivered, flip the K8s HPA cap `realtime.hpa.maxReplicas: 1 → 4` and validate in the Talos lab. This closes the last open ADR-0022 item.

## 2. Non-goals

- **No leader-aware routing logic in API (Pro.Cluster.Router).** The relay is the only consumer in this phase. Other Pro.Cluster components keep their per-node semantics.
- **No event-driven `OnLeadershipChanged` callback.** The relay polls `IsLeader` inside its Forward methods — a property read, no DB call per event. Event-driven notification is a future enhancement if the polling overhead measurably hurts.
- **No leader-election strategy options.** Single backend: Postgres. The transport is already Postgres; reusing the same DB avoids a new infra requirement.
- **No SMB compose changes.** SMB stays single-pod; leader election runs as a no-op (the one pod always wins the lock). Manuales already updated in the previous session — no edits needed.

## 3. Architectural decisions

### Why leader election (and not a messaging-layer fix)

**The duplicate-delivery problem could in principle be solved at the Pro.Push transport layer** by switching from Redis pub/sub + Postgres LISTEN/NOTIFY to Redis Streams consumer groups (XREADGROUP gives exactly-once-per-group delivery, no application coordination needed). After deep analysis of Pro.Push's architecture, this approach was **rejected** for these specific, durable reasons:

1. **Pro.Push has TWO backplanes** — `RedisEventRelay` (pub/sub) AND `PostgresEventRelay` (LISTEN/NOTIFY). Redis Streams has no Postgres-side analog. Switching to Streams collapses the dual-backplane option to Redis-only, removing a deployment knob customers may need.
2. **Pro.Push echo suppression is per-NodeId, not per-pod.** Two pods sharing the same `Asterisk__ClusterNodeId` would already deduplicate each other; two pods with different node IDs would not. The transport-layer mental model never accounted for multi-pod-per-node, so the dedup story is incomplete at the transport.
3. **Closed-source Pro consumers cannot be migrated to consumer groups without rewriting Pro packages.** Analytics, AgentAssist, Dialer, CallAnalytics, EventStore each subscribe via `IPushEventBus.AsObservable().OfType<T>()`. Even if we shipped consumer groups in `RedisEventRelay`, those closed-source consumers' subscription patterns assume broadcast semantics. Some of them (Analytics aggregation) are idempotent and tolerate duplicates; others (Dialer outbound dispatch) absolutely need leader-gating regardless of the messaging layer.
4. **Leader election is foundational for OTHER use cases.** Even with consumer groups for Realtime, we'd still need leader election for: scheduled-report dispatch (cron-like singleton), outbound Dialer campaigns, cluster-wide drain coordination, OIDC token refresh singletons, future cron workers. The primitive belongs in `Pro.Cluster`. Building it once and reusing across consumers is the durable answer.
5. **The Phase A predecessor plan (already shipped) explicitly locked leader-election as the strategy** — the relay's TODO comment (lines 39–45 of `PushToHubRelay.cs`) anticipated exactly this work. Reversing that decision now would create cross-version semantic drift in Pro.Push that's hard to roll forward cleanly.

**Conclusion:** leader election at the application layer is the architecturally correct primary mechanism. Messaging-layer consumer groups remain a viable future complement (Phase A.6 candidate) for tightening Realtime fanout efficiency at very high pod counts, but they neither replace nor block this phase.

### D1 — Leader election piggybacks on `IDistributedLock`
Reuse the existing `Verbara.Sdk.Cluster.Primitives.IDistributedLock` interface (TryAcquireAsync with TTL, re-acquisition refreshes expiry — exact semantics we need). Implement `PostgresDistributedLock` once; leader election becomes a renewal loop on top.

**Why:** Avoids inventing a parallel leadership API; the lock primitive predates this work and was clearly designed for this pattern. A future use case (e.g. Pro.Dialer leader-gating outbound campaign loops) gets the same primitive for free.

### D2 — `PostgresDistributedLock` lives in a NEW shared package `Verbara.Sdk.Cluster.Postgres`
Not in Pro.Cluster.Storage.Postgres. The lock is a generic primitive — both Pro.Cluster and any future SDK consumer can use it independently. Pro.Cluster.Storage.Postgres depends on it transitively.

**Why:** Lower-level primitives belong in the SDK (MIT). Putting the lock impl in Pro would force any open-source consumer to take the Pro commercial license to get cluster-wide locking.

### D3 — Single-row `cluster_distributed_lock` table per resource
```sql
CREATE TABLE cluster_distributed_lock (
    resource TEXT PRIMARY KEY,
    owner TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
);
```
Acquire/renew = single upsert with WHERE clause checking owner-or-expired. No advisory locks, no `FOR UPDATE` (the upsert is atomic and contention is sub-second). Index implicit on PK.

**Why:** Simplest reliable mechanism. ~50 lines of SQL+C#. No PG advisory-lock semantics gotchas. Works under connection-pooling without locking sessions.

### D4 — `IClusterLeader` is PER-RESOURCE, not a global singleton
Different concerns demand independent leaders. Forcing `realtime:fanout` and (future) `dialer:campaign-scheduler` and `scheduled-reports:dispatcher` onto the same pod concentrates unrelated work and creates an artificial coupling. The API exposes per-resource leadership:

```csharp
namespace Verbara.Sdk.Pro.Cluster.Leadership;

public interface IClusterLeader
{
    string Resource { get; }                // e.g. "realtime:fanout:leader"
    bool IsLeader { get; }                  // synchronous snapshot for THIS pod
    string? CurrentLeaderInstanceId { get; } // diagnostic — who holds it
}
```

Registration is explicit per resource:
```csharp
services.AddVerbaraCluster(opts =>
{
    opts.UsePostgresLockBackend(connectionStringName: "Cluster");
    opts.RegisterLeader("realtime:fanout:leader");   // Realtime registers exactly one
    // opts.RegisterLeader("dialer:campaigns:leader"); // future Dialer use case
});
```

Each `RegisterLeader` call:
- Adds a keyed `IClusterLeader` singleton (key = resource string)
- Registers a hosted `LeaderElectionService` that runs the renewal loop ONLY for that resource
- Surfaces two Prometheus metrics (see D8) keyed by `resource`

Consumers inject the specific leader via keyed DI:
```csharp
public PushToHubRelay([FromKeyedServices("realtime:fanout:leader")] IClusterLeader leader, ...)
{
    _fanoutLeader = leader;
}
```

**Why per-resource:** Realtime is the FIRST use case but not the last. The interface must compose for the next 5 use cases without rework. Singleton leadership (`ClusterManager.Leadership`) bakes in a wrong-by-construction assumption — multiple concerns CAN'T share the same leader without unrelated coupling. Per-resource elects independently and scales operationally.

**Keyed DI AOT note:** `Microsoft.Extensions.DependencyInjection` 8.0+ keyed services are source-gen-friendly when keys are constant strings (no reflection at resolution time). Both `AddKeyedSingleton` and `[FromKeyedServices]` are AOT-clean — verified against `<IsAotCompatible>true>` in current SDK packages.

### D5 — Renewal cadence: 10 s acquire / 30 s TTL
Standard pattern. 20-second grace window absorbs Postgres latency, node clock skew (NTP), and brief network partitions. A pod that fails to renew within 30 s loses leadership; the next renewal attempt by any pod elects a new leader within 10 s. Worst-case leaderless window: ~10 s. Acceptable for the SignalR fanout use case (clients reconnect on disconnect, individual missed-events are negligible vs duplicate-events which double cost).

### D6 — Realtime polls `IsLeader` in `PushToHubRelay.ForwardXxx` methods, not in `StartAsync`
Always subscribe to `IPushEventBus` (lightweight), but short-circuit the `_hubContext.Clients.Group(...)` call when `!IsLeader`. Each Forward call evaluates one boolean read.

**Why:** Survives leadership transitions without dispose/recreate dance. Subscription cost is negligible. Simpler than tearing down and rebuilding subscriptions on every leadership flip. The volatile read (`_isLeader` field on `LeaderElectionService`) is essentially free; sub-nanosecond per call.

### D7 — Migration in `Verbara.Sdk.Cluster.Postgres`, not Pro
New migration `V001__DistributedLockSchema.sql` ships with the new SDK package. Pro.Cluster.Storage.Postgres references the package and runs its migrations as part of the existing migration chain.

### D8 — Observability is mandatory, not optional
Operating a leader-elected service in production demands visibility. The leader-election service exposes:

**Prometheus metrics** (via existing `Verbara.Sdk.Observability` or System.Diagnostics.Metrics):
- `verbara_cluster_leader_is_self{resource="...",instance="..."}` — gauge, 0/1. 1 if THIS pod is leader for this resource.
- `verbara_cluster_leader_acquisitions_total{resource="...",outcome="won|lost|renewed|released"}` — counter incremented on every loop iteration's outcome.
- `verbara_cluster_leader_lease_age_seconds{resource="..."}` — gauge, seconds since the current leader (any pod) acquired/renewed. Detects stuck-leader scenarios.

**Structured logs** (`LoggerMessage` source-gen, no boxing):
- Info on every transition: `"Leadership transition for {Resource}: {OldState} → {NewState}, leader instance={LeaderInstanceId}"`
- Warn on consecutive renewal failures (≥2): `"Failed to renew leadership for {Resource}: {FailureCount} consecutive failures, expires_at={ExpiresAt}"`
- Trace on every short-circuited forward in PushToHubRelay (sampled): `"Skipping forward of {EventType} — not leader for {Resource}"`

**No new Grafana dashboard panels required** for Phase A.5 — alerts (BlackboxJourneyDown etc.) already cover the fanout-stopped failure mode. Future enhancement: add a "leadership health" dashboard panel.

### D9 — Clean-shutdown release is required, not best-effort
The `LeaderElectionService.StopAsync` MUST call `IDistributedLock.ReleaseAsync` for the held resource. This avoids the 30s TTL wait on graceful pod shutdown — the next pod can elect within ~10s instead of ~30-40s.

For UN-graceful shutdown (kill -9, OOM, node failure), TTL expiry remains the fallback. Operationally documented.

### D10 — Fencing tokens explicitly NOT needed for Realtime; reserved for future
Fencing tokens (monotonic IDs incremented per lock acquisition, attached to side-effect operations to detect stale leader actions) are a defense against split-brain in scenarios where a stale leader continues to mutate shared state after losing leadership. For Realtime's fanout use case:
- The "side effect" is publishing a SignalR message
- Worst case of a stale-leader forward: a momentary duplicate during transition (~ms window)
- Impact: indistinguishable from the existing pre-Phase-A.5 behavior; harmless

Fencing tokens become relevant when DIALER ports to leader election (a stale leader making outbound calls to numbers already drawn by the new leader = bad). At that point, extend `IClusterLeader` with a `LeaderEpoch` property (monotonic int) and require callers to pass it through to their downstream coordinator. **Not in scope for A.5.**

## 4. Implementation work breakdown

### 4.1 — SDK (Verbara.Sdk) — new package `Verbara.Sdk.Cluster.Postgres`

Files (new):
- `src/Verbara.Sdk.Cluster.Postgres/Verbara.Sdk.Cluster.Postgres.csproj` — net10.0, AOT-compatible, PackageReference `Npgsql` + `Verbara.Sdk.Data.Npgsql` + ProjectReference `Verbara.Sdk.Cluster.Primitives`
- `src/Verbara.Sdk.Cluster.Postgres/PostgresDistributedLock.cs` — impl of `IDistributedLock`
- `src/Verbara.Sdk.Cluster.Postgres/DependencyInjection/ClusterPostgresServiceCollectionExtensions.cs` — `AddPostgresDistributedLock(NpgsqlDataSource)` extension
- `src/Verbara.Sdk.Cluster.Postgres/Migrations/V001__DistributedLockSchema.sql` — embedded resource
- `src/Verbara.Sdk.Cluster.Postgres/Migrations/MigrationRunner.cs` — invokes the embedded migration (consumers call once at startup)
- `src/Verbara.Sdk.Cluster.Postgres/PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt` — public-api-analyzer files (zero shipped, full unshipped)

Files (modified):
- `Verbara.Sdk.slnx` — register the new project
- `Directory.Packages.props` (Sdk) — no new entries (uses existing Npgsql + Sdk.Data.Npgsql)

Tests (new):
- `Tests/Verbara.Sdk.Cluster.Postgres.Tests/PostgresDistributedLockTests.cs` — uses Testcontainers Postgres 18 or a real PG fixture; mirrors `InMemoryDistributedLockTests` test names so coverage parity is enforceable
- Covers: acquire when free, reject when held by other, re-acquire by same owner refreshes TTL, release no-op on non-owner, expiry releases automatically, concurrent acquire by 2 owners → exactly-one wins (8 tests minimum)

### 4.2 — Pro.Cluster — new IClusterLeader (per-resource) + LeaderElectionService

Files (new):
- `src/Verbara.Sdk.Pro.Cluster/Leadership/IClusterLeader.cs` — public interface (per-resource)
- `src/Verbara.Sdk.Pro.Cluster/Leadership/LeaderElectionService.cs` — `BackgroundService` running the renewal loop for ONE resource; implements `IClusterLeader`. One instance per registered resource.
- `src/Verbara.Sdk.Pro.Cluster/Leadership/ClusterLeadershipOptions.cs` — `RenewalInterval` (default 10s), `LeaseDuration` (default 30s), `InstanceId` (default `Environment.MachineName`, overridable via `Cluster:InstanceId` config / env)
- `src/Verbara.Sdk.Pro.Cluster/Leadership/ClusterLeadershipMetrics.cs` — Prometheus instrument definitions (per D8)
- `src/Verbara.Sdk.Pro.Cluster/Leadership/LeadershipLog.cs` — `LoggerMessage` source-gen for the 3 structured log entries (per D8)

Files (modified):
- `src/Verbara.Sdk.Pro.Cluster/DependencyInjection/ClusterServiceCollectionExtensions.cs`:
  - `AddVerbaraCluster(Action<VerbaraClusterOptionsBuilder>)` — new builder API.
  - Builder exposes `UsePostgresLockBackend(connectionStringName)` and `RegisterLeader(string resource)`.
  - `RegisterLeader` calls `services.AddKeyedSingleton<IClusterLeader>(resource, ...)` AND `services.AddHostedService<LeaderElectionService>(...)` bound to that resource via factory.
- `src/Verbara.Sdk.Pro.Cluster/Verbara.Sdk.Pro.Cluster.csproj` — ProjectReference to new `Verbara.Sdk.Cluster.Postgres`
- `PublicAPI.Unshipped.txt` — new interface + extension methods

Tests (new):
- `tests/Verbara.Sdk.Pro.Cluster.Tests/LeaderElectionServiceTests.cs` — uses fake `IDistributedLock` (the in-memory one already exists in Sdk.Cluster.Primitives.Tests; reuse via InternalsVisibleTo or duplicate). Tests:
  1. `IsLeader_ShouldBeTrue_WhenInitialAcquireSucceeds`
  2. `IsLeader_ShouldBeFalse_WhenInitialAcquireFails`
  3. `IsLeader_ShouldTransitionToFalse_WhenRenewalFails`
  4. `IsLeader_ShouldRecover_WhenRenewalSucceedsAfterFailure`
  5. `CurrentLeaderInstanceId_ShouldReflectOwner_FromLockProbe`
  6. `StopAsync_ShouldReleaseLock_WhenHeld`
  7. `StopAsync_ShouldNotThrow_WhenNotHeld`
  8. `MultipleResources_ShouldElectIndependently_WhenRegisteredTogether`
- `tests/Verbara.Sdk.Pro.Cluster.Tests/ClusterLeadershipMetricsTests.cs` — asserts metric emission on every state transition + counter increment per renewal outcome (4 tests)

### 4.3 — Pro.Cluster.Storage.Postgres — wire migration into existing chain

Files (modified):
- `src/Verbara.Sdk.Pro.Cluster.Storage.Postgres/PostgresClusterTransport.cs` (or wherever migrations are invoked) — invoke `Verbara.Sdk.Cluster.Postgres.MigrationRunner.EnsureSchema(...)` before Pro.Cluster's own migrations

### 4.4 — Platform.Realtime — wire Pro.Cluster + leader-gate the relay

Files (modified):
- `src/Verbara.Platform.Realtime/Program.cs` — wire cluster + register the fanout-leader resource:
  ```csharp
  builder.Services.AddVerbaraCluster(opts =>
  {
      opts.UsePostgresLockBackend(connectionStringName: "Cluster");
      opts.RegisterLeader(RealtimeLeaderResources.Fanout);
  });
  ```
  Config: `ConnectionStrings:Cluster` (default to `ConnectionStrings:Postgres`), `Cluster:InstanceId` (default = downward-API `POD_NAME` if present, else `Environment.MachineName`)
- `src/Verbara.Platform.Realtime/Services/RealtimeLeaderResources.cs` (new) — central string constants for resource names (`Fanout = "realtime:fanout:leader"`). Avoids stringly-typed bugs.
- `src/Verbara.Platform.Realtime/Services/PushToHubRelay.cs`:
  - Constructor: inject `[FromKeyedServices(RealtimeLeaderResources.Fanout)] IClusterLeader fanoutLeader`
  - `ForwardConversation`, `ForwardAgent`, `ForwardClusterNode`: early-return when `!_fanoutLeader.IsLeader`; structured Trace log via LoggerMessage source-gen
  - Remove the existing TODO comment block (lines 39–45) that anticipated this work
- `src/Verbara.Platform.Realtime/Verbara.Platform.Realtime.csproj` — PackageReference `Verbara.Sdk.Pro.Cluster` + `Verbara.Sdk.Cluster.Postgres`

Tests (modified):
- `tests/Verbara.Platform.Realtime.Tests/PushToHubRelayTests.cs` — add tests with a `FakeClusterLeader` (mutable `IsLeader`):
  1. `Forward_ShouldNotInvokeHubContext_WhenNotLeader`
  2. `Forward_ShouldInvokeHubContext_WhenLeader`
  3. `Forward_ShouldRecover_AfterLeadershipRegained` (toggle IsLeader true→false→true, assert calls only fire during true windows)
  4. `Forward_ShouldEmitTraceLog_WhenSkippingDueToNotLeader` (verifies the structured log fires; helps support investigations)

### 4.5 — K8s Helm chart — flip HPA + topology spread + cluster config

Files (modified):
- `infra/k8s/helm/platform/values.yaml`:
  - `realtime.hpa.maxReplicas: 1` → `4`
  - Add `realtime.cluster.instanceIdFromEnv: POD_NAME` (Pod uses downward API for unique instance ID)
  - Update the "Phase A.2+A.3 ships single-pod because…" comment block to reference Phase A.5 closure
- `infra/k8s/helm/platform/templates/realtime-deployment.yaml`:
  - Add `topologySpreadConstraints` with `maxSkew: 1`, `topologyKey: kubernetes.io/hostname`, `whenUnsatisfiable: ScheduleAnyway` (matches the soft anti-affinity already there but explicit)
  - Add `POD_NAME` env var via downward API (`fieldRef: metadata.name`) for `Cluster__InstanceId`
  - Add `ConnectionStrings__Cluster` env var (default to `ConnectionStrings__Postgres` if not split)
- `infra/k8s/helm/platform/templates/realtime-hpa.yaml` — no edits, the value change in values.yaml propagates

### 4.6 — Migrations: list one new

| Repo | Path | Description |
|------|------|-------------|
| SDK | `src/Verbara.Sdk.Cluster.Postgres/Migrations/V001__DistributedLockSchema.sql` | Creates `cluster_distributed_lock` table (resource PK, owner, expires_at) |

## 5. Release sequence

Strict order (dependency chain enforces):

1. **SDK 2.2.1** — pack `Verbara.Sdk.Cluster.Postgres` + ship to `/media/Data/Source/Verbara/local-nuget-feed/`
   - `dotnet pack -c Release -o /media/Data/Source/Verbara/local-nuget-feed/`
   - Clear NuGet cache for any consumer: `rm -rf ~/.nuget/packages/verbara.sdk.cluster.postgres*/`
2. **Pro 2.5.1-pro** — bump version, pack, ship to local feed + sync to `Verbara.Platform/local-nuget-feed/` (Docker context only sees the repo-local copy)
   - `dotnet pack -c Release -o /media/Data/Source/Verbara/local-nuget-feed/`
   - `cp /media/Data/Source/Verbara/local-nuget-feed/Verbara.Sdk.Pro.Cluster.2.5.1-pro.nupkg /media/Data/Source/Verbara/Verbara.Platform/local-nuget-feed/`
   - `rm -rf ~/.nuget/packages/verbara.sdk.pro.cluster*/`
3. **Platform v2.4.2** — bump version in `Directory.Build.props`, restore against new Pro, full test suite green, build native AOT publish
4. **Re-build & re-sign 4 ghcr.io images** at `v2.4.2`: api / realtime / renderer / mail. Use the same cosign signing workflow that produced v2.4.1.
5. **Push images** to `ghcr.io/verbara/platform/*:v2.4.2`. Verify signatures with `cosign verify ...`
6. **Update verbara-website digest authorization** for v2.4.2 (image-digest binding per ADR-0011)
7. **Tag releases**:
   - `Verbara.Sdk` → `v2.2.1`
   - `Verbara.Sdk.Pro` → `v2.5.1-pro`
   - `Verbara.Platform` → `v2.4.2`

## 6. Smoke test protocol (Talos lab)

Pre-conditions: Talos cluster up (currently 4 nodes Ready). Same `r55-staging` namespace used for B-LK rounds.

1. `helm upgrade platform ./infra/k8s/helm/platform -n r55-staging --set realtime.image.tag=v2.4.2 --set api.image.tag=v2.4.2 --wait`
2. Watch `kubectl -n r55-staging get pods -l app.kubernetes.io/name=platform-realtime -w` until 4 replicas Ready
3. Verify single leader: `kubectl -n r55-staging exec deploy/platform-postgres -- psql -U verbara -d verbara -c "SELECT * FROM cluster_distributed_lock WHERE resource = 'cluster:leader:singleton';"` → exactly one row, `expires_at` in future
4. Watch each realtime pod's logs for `IsLeader = true|false` startup log; expect exactly one `true` across the 4 pods
5. Connect SignalR client to each of the 4 pods via direct service (bypass gateway), subscribe to a tenant group, fire one conversation state change event via API, assert: client receives event EXACTLY ONCE (not 4x). Repeat 5 times. **No duplicate delivery = PASS gate.**
6. Forcibly delete the leader pod: `kubectl -n r55-staging delete pod platform-realtime-<leader>` ; assert: within 30 s, a different pod reports `IsLeader=true`; SignalR delivery resumes within that window
7. Document in `docs/operations/phase-a5-smoke-test-<date>.md`

## 7. Exit criteria

All must be green before plan moves to `docs/plans/completed/`:

- [ ] SDK 2.2.1 packed, signed, on local feed; `Verbara.Sdk.Cluster.Postgres` AOT-compatible (zero IL2026/IL3050)
- [ ] Pro 2.5.1-pro packed, on local feed + Platform repo copy; `Verbara.Sdk.Pro.Cluster` AOT-compatible
- [ ] Platform v2.4.2 builds Native AOT (0 diagnostics); 943 + 22 + 72 + new tests all green
- [ ] `cluster_distributed_lock` migration applies cleanly to fresh Postgres 18 + idempotent rerun
- [ ] 4 cosign-signed images on ghcr.io at `v2.4.2`
- [ ] Talos smoke test PASS: 4 replicas, single leader at any time, zero duplicate SignalR delivery in 5 of 5 trials, leadership failover within 30 s after pod kill
- [ ] `docs/operations/phase-a5-smoke-test-<date>.md` archived
- [ ] Plan `git mv` to `docs/plans/completed/`
- [ ] Roadmap memory + current-position memory updated: Phase A.5 closed, ADR-0022 TRACK FULLY CLOSED (no open items)

## 8. Risks & mitigations

| Risk | Mitigation |
|------|------------|
| Postgres connection storms (each pod hammers the lock table every 10 s × 4 pods = 0.4 qps — negligible) | Renewal loop uses the same `NpgsqlDataSource` as the rest of Realtime; pool sizing already validated for hundreds of req/s |
| Clock skew between pods → premature TTL expiry | TTL is server-side (`NOW()` in the SQL); pod clocks irrelevant for the comparison |
| Migration ordering: V001 of Cluster.Postgres ships separately from Pro.Cluster.Storage.Postgres migrations | Both runners are idempotent (`CREATE TABLE IF NOT EXISTS`); order doesn't matter |
| Single Postgres = single point of failure for leader election | Same as the rest of the platform — Postgres failover is a separate concern (R5.5 chaos validated 16 s primary failover) |
| Leaderless window during election (~10 s) drops SignalR fanout | Acceptable: clients reconnect on disconnect; cross-pod fanout missing for ≤10 s is preferable to duplicate fanout in steady-state. Documented in operations notes. |
| 2 pods believe they are leader simultaneously due to race | Postgres atomic upsert prevents this; the `WHERE owner=$1 OR expires_at<NOW()` clause guarantees exactly-one wins per moment |

## 9. Execution plan (subagent-driven, FCM batching per user CLAUDE.md)

**Phase A — Foundation (parallel subagents in one batch)**
- Subagent A1: scaffold `Verbara.Sdk.Cluster.Postgres` package (csproj + DI extension + migration file)
- Subagent A2: scaffold `IClusterLeadership` + `LeaderElectionService` skeleton in Pro.Cluster
- Subagent A3: prepare K8s Helm value changes (no apply yet)

**Phase B — Critical components (focused individual subagents)**
- Subagent B1: implement `PostgresDistributedLock` + 8 TDD tests against real Postgres
- Subagent B2: implement `LeaderElectionService` background loop + 5–6 TDD tests against fake `IDistributedLock`
- Subagent B3: wire `IClusterLeadership` into `ClusterManager` + DI; update PublicAPI.Unshipped.txt

**Phase C — Integration (batch)**
- Subagent C1: pack SDK 2.2.1 + Pro 2.5.1-pro → local feed; clear NuGet cache; verify restore in Platform
- Subagent C2: wire Pro.Cluster into Realtime `Program.cs` + leader-gate `PushToHubRelay` + 3 new TDD tests
- Subagent C3: Helm chart edits + commit
- Subagent C4: full Platform AOT build + test run; gate on green
- Subagent C5: bump Platform to v2.4.2 + release commit
- Subagent C6 (manual / out-of-band): docker build + cosign sign + push 4 images to ghcr.io
- Subagent C7 (manual / out-of-band): Talos lab smoke test

Each phase awaits explicit confirmation before kicking off — no autonomous rolling-ahead through release boundaries.

## 10. Out of scope / explicit deferrals (tracked for follow-up)

- **Phase A.6 — Pro.Push consumer groups (Redis Streams XREADGROUP).** Architecturally cleaner messaging-layer fix for duplicate-delivery; would let Realtime fanout scale truly N-way instead of leader-concentrated. Rejected for Phase A.5 (see §3 "Why leader election"). Re-evaluate when Realtime fanout becomes a measured bottleneck or when consumer-group semantics are needed for a NEW use case that can't tolerate the leader-bottleneck pattern. Track in roadmap memory.
- **Fencing tokens (`LeaderEpoch`).** Needed when Dialer ports outbound campaign dispatch to leader election (a stale leader making outbound calls to numbers already drawn by the new leader = bad). Extend `IClusterLeader` with a monotonic `LeaderEpoch` property at that point. Reserved interface slot.
- **Event-driven `OnLeadershipChanged`.** Polling is sufficient for current consumers (PushToHubRelay does a property read on every forward — sub-nanosecond). Add an event when polling overhead is measurably non-zero OR when a consumer needs sub-10s leadership-transition reaction time.
- **K8s LeaseLock-based election.** An alternative implementation using K8s `coordination.k8s.io` Lease objects would remove the Postgres dependency for K8s-only deployments. Rejected for unified-design (Pro.Cluster already requires Postgres for transport; adding K8s API dependency would fragment by deployment target). Defer until customer requires it.
- **madelson/DistributedLock NuGet.** Mature 3rd-party library that already implements Postgres-backed locks. Rejected because (a) AOT compatibility is unverified — they use reflection in lock-factory paths; (b) interface is broader than `IDistributedLock` and would require an adapter shim anyway; (c) ~50 LoC of hand-rolled impl is simpler than the dependency surface. Re-evaluate if maintenance becomes painful.
- **Pg_advisory_lock-based variant.** Native PG advisory locks (`pg_try_advisory_lock`) auto-release on connection close (zero TTL lag on crash). Rejected because they require pinning one Npgsql connection per resource per pod (`AddVerbaraCluster` would need to allocate dedicated connections outside the pool); operational visibility is worse (`pg_locks` view is opaque); the table-based TTL approach uses 0 pool connections in steady state and is queryable via plain SELECT. Documented for future re-evaluation if 30s TTL recovery becomes a customer complaint.
- **Pro.Cluster routing leader-awareness.** The `Router` component does not consult `IsLeader` in this phase. If needed for future use cases (Dialer outbound campaign serialization, scheduled-report deduplication), add then via the SAME `RegisterLeader("dialer:campaigns:leader")` mechanism.
- **Lock TTL tuning per resource.** The lock primitive accepts a TTL parameter; the leader-election service hard-codes 30s for Phase A.5 simplicity. Tunable via `ClusterLeadershipOptions` if measured pain emerges.
- **Multi-cluster / federation.** Single-cluster only. The lock is local to one Postgres database. Cross-DC failover is a separate ADR.

## 11. Decision audit trail (alternatives rejected, with rationale)

| Alternative | Reason rejected | Re-evaluate when |
|-------------|----------------|------------------|
| Redis Streams consumer groups in Pro.Push | Locks design to Redis-only; closed-source Pro consumers can't migrate; leader election still needed for Dialer/scheduled-reports regardless | Realtime fanout becomes measured bottleneck OR a new use case demands consumer-group semantics |
| madelson/DistributedLock library | AOT unverified; dependency surface > our IDistributedLock interface; ~50 LoC for hand-roll | We need a backend we don't have (SQL Server, Azure Blob, etc.) |
| Postgres advisory locks (`pg_try_advisory_lock`) | Requires pinning Npgsql connections per resource; pool fragmentation; opaque `pg_locks` debugging | 30s TTL recovery becomes operational pain |
| K8s LeaseLock (`coordination.k8s.io`) | Fragments by deployment target (K8s vs Docker vs SMB); adds K8s API dependency to pods | Customer constraints demand zero-Postgres for leader coordination |
| LISTEN/NOTIFY-based leadership change notification | Adds dedicated PG connection per pod; polling overhead is already negligible | Polling overhead measurably hurts OR sub-10s transitions become required |
| Singleton global leader (`ClusterManager.Leadership`) | Couples unrelated concerns (Realtime fanout + Dialer + scheduled-reports) onto one pod | N/A — this is a wrong-by-construction design |
| Fencing tokens in Phase A.5 | Realtime forwards are idempotent in effect; tokens are overhead without payoff | Dialer ports to leader election (real side-effects depend on leader epoch) |
| Event-driven OnLeadershipChanged in Phase A.5 | Polling is sufficient; cost is one volatile read per forward | Consumer needs sub-poll-interval reaction time OR polling becomes hot-path |
