# Phase A.5 — Leader-election smoke test (Option A, docker-compose)

> **Date:** 2026-05-23
> **Validates:** ADR-0022 Phase A.5 — per-resource leader election from `Verbara.Sdk.Pro.Cluster` 2.5.1-pro layered on `Verbara.Sdk.Cluster.Postgres` (SDK 2.2.1) `PostgresDistributedLock`.
> **Verdict:** ✅ PASS — single leader on cold start, graceful failover in 4 s, ungraceful failover in 14 s, transitions logged correctly.
> **Out of scope:** the Talos lab smoke test (Option B) — see `docs/plans/active/2026-05-23-phase-a5-talos-smoke-test.md`. The Realtime fanout gate inside `PushToHubRelay.ForwardXxx` is covered by the four `PushToHubRelayTests` shipped in Phase C.2 commit `fe8a1938`, so this smoke test focuses on the leader-election lifecycle end-to-end against real infrastructure.

## 1. Scope & rationale (Option A vs alternatives)

The Talos lab (`r55-platform` namespace) is currently on **v2.3.1** — two minor versions behind v2.4.2 and pre-Phase-A SignalR-Hub-extraction. Running the Plan §6 smoke test there requires a full v2.3.1 → v2.4.2 migration which is large enough to belong to its own plan (see `docs/plans/active/2026-05-23-lab-migration-v2.3.1-to-v2.4.2.md`, Plan C).

To unblock Phase A.5 closure without coupling it to that migration, this smoke test runs the v2.4.2 Realtime image in a **self-contained docker-compose stack** that mirrors production topology at the leader-election layer:
- 1× Postgres 18-alpine (cluster_distributed_lock host)
- 1× Redis 8-alpine (SignalR backplane parity; not strictly required for the election but Realtime startup expects it)
- 4× `ghcr.io/verbara/platform/realtime:v2.4.2` instances with unique container hostnames

Network: docker-compose default bridge. No SignalR clients connected — the per-event short-circuit inside `PushToHubRelay.Forward{Conversation,Agent,ClusterNode}` is covered by the four new unit tests in [tests/Verbara.Platform.Realtime.Tests/Services/PushToHubRelayTests.cs](../../tests/Verbara.Platform.Realtime.Tests/Services/PushToHubRelayTests.cs) which pin the gate behavior with a `FakeClusterLeader`. The remaining unknowns this smoke test pins are end-to-end:

1. **Cold-start election** — N pods boot simultaneously, exactly one wins.
2. **Graceful failover** — `docker stop` (SIGTERM 10 s grace) → `StopAsync.ReleaseAsync` runs → next renewal cycle elects a successor (D9 of the plan).
3. **Ungraceful failover** — `docker kill` (SIGKILL, no grace) → lock survives TTL → successor elected on TTL expiry (default safety net).

## 2. Configuration (accelerated for tighter loops)

Production defaults are `RenewalInterval = 10 s` / `LeaseDuration = 30 s` (`ClusterLeadershipOptions`). The smoke-test compose overrides via `Cluster__Leadership__*` to:

| Field | Prod | Smoke | Reason |
|------|------|-------|--------|
| `RenewalInterval` | 10 s | **5 s** | Tighter loop ⇒ shorter total test wall-clock without changing semantics |
| `LeaseDuration` | 30 s | **15 s** | Same; ungraceful-failover ceiling drops from ~40 s to ~20 s |
| `InstanceId` | downward-API `POD_NAME` | container hostname (Docker default `Environment.MachineName`) | The env-var path is `Cluster__Leadership__InstanceId`; the smoke compose left it unset to exercise the default (this is what bare-metal `Environment.MachineName` resolves to in production deployments without K8s downward API) |

Everything else matches the production `Cluster:Leadership:*` defaults. The proportional behavior between graceful and ungraceful failover scales linearly to prod: multiply by 2.

## 3. Test sequence and results

### Test 1 — Cold-start election (T = 0 → T = 18 s)

- All 4 pods came healthy within 10 s of `docker compose up`.
- At T+18 s the row count in `cluster_distributed_lock` was **exactly 1**.
- Owner: `361244ab1544` → `realtime-4` container.
- TTL remaining: 11.94 s of the 15 s lease (renewal at ~T+15 s, observation at T+18 s).
- Only `realtime-4` emitted a `Leadership transition for realtime:fanout:leader: follower -> leader` log line. The other three pods stayed silent — by design, the renewal-loop's `HandleTransition` only logs on edge transitions (`follower → leader`, `leader → released`); a follower that just-keeps-being-a-follower writes nothing.

```text
=== cluster_distributed_lock at T+18s ===
        resource        |    owner     |          expires_at           |  ttl_remaining
------------------------+--------------+-------------------------------+-----------------
 realtime:fanout:leader | 361244ab1544 | 2026-05-23 10:36:22.783046+00 | 00:00:11.940261
(1 row)

realtime-4 logs:
  Leadership transition for realtime:fanout:leader: follower -> leader, leaderInstance=361244ab1544
```

**Pass criteria:** ≤1 lock row at all times ✓; exactly one pod reports leadership ✓.

### Test 2 — Graceful failover (`docker stop`, T = 0 → T = 4 s)

- `docker stop phase-a5-realtime-4` sends SIGTERM with a 10 s grace before SIGKILL.
- Within Realtime's `StopAsync`, the `LeaderElectionService` calls `IDistributedLock.ReleaseAsync(resource, ownerInstanceId)` (D9 of plan §3) which executes `DELETE FROM cluster_distributed_lock WHERE resource = $1 AND owner = $2`.

Polling the lock table at 1 s intervals captured the sequence:
```text
T+1s : table EMPTY (lock RELEASED by departing leader — D9 clean shutdown)
T+2s : table EMPTY
T+3s : table EMPTY
T+4s : NEW LEADER = 2e98ae6ac430  (realtime-3)
```

- `realtime-4` logged the closing transition `leader -> released, leaderInstance=(null)` before exit.
- `realtime-3` logged `follower -> leader, leaderInstance=2e98ae6ac430` once its next renewal cycle ran (within the 5 s `RenewalInterval`).

**Pass criteria:** lock released on graceful shutdown ✓; successor elected within one renewal interval ✓.

**Total handoff window: 4 s** with `RenewalInterval=5 s`. In production (`RenewalInterval=10 s`) this scales to **~10 s** worst case for graceful pod replacement during a rolling deploy.

### Test 3 — Ungraceful failover (`docker kill`, T = 0 → T = 14 s)

- `docker kill phase-a5-realtime-3` sends SIGKILL — no grace, no `StopAsync`, no `ReleaseAsync`.
- The lock row remains in Postgres with the dead leader's instance ID until `expires_at < NOW()`.
- Surviving pods' renewal cycles continue calling `TryAcquireAsync(resource, ownInstanceId, ttl)` every `RenewalInterval`. While the dead lease is still in-window the SQL `WHERE (owner = $1 OR expires_at < NOW())` rejects them (they're neither the lock's owner nor past expiry). `PostgresDistributedLock.TryAcquireAsync` returns `false`. No exception, no warning log fires — the rejection is the normal "lost" outcome.
- Once the dead lease expires at T+13.5 s, the next surviving pod's renewal call wins the upsert.

Polling captured every second:
```text
T+1s : stale lock still owned by dead leader  ttl=00:00:12.259809
...
T+13s : stale lock still owned by dead leader ttl=00:00:00.014507
T+14s : NEW LEADER = 88c4d7e08d40   (realtime-2)
```

**Total handoff window: 14 s** = remaining TTL (13.5 s) + slack until the next pod's renewal cycle hit it (~0.5 s).

In production with `LeaseDuration=30 s` + `RenewalInterval=10 s`, the worst-case ungraceful window is **~30–40 s**. This matches the `Risks & mitigations` row in the plan ("Leaderless window during election … Acceptable: clients reconnect on disconnect; cross-pod fanout missing for ≤ 30 s is preferable to duplicate fanout in steady-state"). The plan §8 currently undersells this with "≤ 10 s" — that figure was for graceful failover only. **Plan update needed**: see §6 below.

**Pass criteria:** no double-leader window observed ✓; successor elected within `LeaseDuration + RenewalInterval` ✓; no spurious renewal-failure logs on followers (`grep -c "Failed to renew"` returned 0 across the 14 s window).

### Test summary

| Test | Expected | Measured | Verdict |
|------|----------|----------|---------|
| Single leader on cold start | 1 lock row, 1 transition log | 1 row, owner=realtime-4 | ✅ |
| Graceful failover handoff | ≤ 1 × `RenewalInterval` after Release | 4 s (with RI=5 s) | ✅ |
| Ungraceful failover handoff | ≤ `LeaseDuration + RenewalInterval` after kill | 14 s (with LD=15 s, RI=5 s) | ✅ |
| Leader transition log entries | Exactly on edge transitions; no spam | 1 entry per real transition on each affected pod | ✅ |
| Renewal-failure warnings during contended TTL window | 0 (rejections aren't exceptions) | 0 | ✅ |

## 4. Production extrapolation

| Scenario | Prod config (`RI=10 s`, `LD=30 s`) |
|---------|-----------------------------------|
| Graceful pod replace (rolling deploy of Realtime) | ~ 10 s leaderless window |
| Pod OOM / node loss (ungraceful) | ~ 30–40 s leaderless window |
| Postgres primary failover (CNPG, R5.5 chaos C-LK measured 16 s) | ~ 30–46 s combined leaderless (TTL counting during DB outage + post-recovery acquisition race) |

During any leaderless window:
- The single live leader pod (if any) continues to forward.
- During pure leaderless gap: **no pod publishes Pro.Push events to SignalR clients via `_hubContext.Clients.Group(...)`** — clients lose live state updates briefly. SignalR keepalive maintains the WebSocket; clients reconnect on disconnect. Missed transient state changes (presence, typing-indicator) are eventually-consistent via the next event after recovery.
- This is the correct trade-off vs the alternative (multiple pods publishing → SignalR backplane fans each to all connection-owners → clients receive N duplicate events per state change).

## 5. Known gaps (carry forward to Plan C)

### Gap 1 — `V001__DistributedLockSchema.sql` is NOT auto-invoked on startup

The new SDK package `Verbara.Sdk.Cluster.Postgres` ships `MigrationRunner.EnsureSchemaAsync(...)` but nothing wires it into a host startup path. `Verbara.Sdk.Pro.Cluster.Storage.Postgres` runs its own `SchemaMigrator.EnsureSchemaAsync(...)` for `cluster_node_*` tables but does NOT also call the new SDK package's migration. `Verbara.Platform.Realtime/Program.cs` registers DI but does not invoke the migration explicitly.

**Smoke-test workaround used here:** the `postgres-init.sql` file pre-creates the `cluster_distributed_lock` table on Postgres container init (`docker-entrypoint-initdb.d`). This works because the table schema is idempotent (`CREATE TABLE IF NOT EXISTS`).

**Production fix (Plan C):** ship a v2.4.3 hotfix that calls
```csharp
await MigrationRunner.EnsureSchemaAsync(
    dataSource: app.Services.GetRequiredKeyedService<NpgsqlDataSource>("Cluster"),
    logger: app.Services.GetRequiredService<ILogger<Program>>(),
    ct: app.Lifetime.ApplicationStopping);
```
inside `Verbara.Platform.Realtime/Program.cs` after `builder.Build()` and before `app.Run()`. Cost: minor Realtime image rebuild + repush + re-sign + verbara-website digest reauth at v2.4.3. Pro and SDK do not need to change.

### Gap 2 — `Cluster__InstanceId` env path documentation drift

The chart's downward-API env var `Cluster__InstanceId` (set to `$(POD_NAME)`) does NOT map to the `ClusterLeadershipOptions.InstanceId` property, which is reached via `Cluster__Leadership__InstanceId`. With the current chart wiring, pods all run with `Environment.MachineName` (the K8s pod name, which is unique but not the one chosen by chart authors). Functionally fine — uniqueness is preserved — but the chart's downward-API env is dead code.

**Fix (Plan C):** rename the chart env var to `Cluster__Leadership__InstanceId` (one line in `realtime-deployment.yaml`), OR (preferred) document that `Environment.MachineName == POD_NAME` in K8s and remove the explicit env var entirely. The latter is simpler and the chart already works correctly without it.

### Gap 3 — Plan §8 risk row underestimates ungraceful failover window

Plan §8 currently says "Leaderless window during election (~10 s) drops SignalR fanout". Smoke test measured **14 s** with smoke-config (LD=15 s) and the proportional figure for prod (LD=30 s) is **~30–40 s**. Update the row when the plan is moved to `completed/`.

## 6. Artifacts

- Compose file used: `/tmp/phase-a5-smoke/docker-compose.smoke-test.yml` (transient — not committed; copy below for repro).
- Postgres init: `/tmp/phase-a5-smoke/postgres-init.sql` (transient).
- Test stack was fully torn down (`docker compose down --volumes`) after the smoke test — no residual containers, volumes, or networks left behind.

Repro from scratch:
```bash
mkdir -p /tmp/phase-a5-smoke && cd /tmp/phase-a5-smoke
# (paste compose + init from this report)
docker compose -f docker-compose.smoke-test.yml up -d
# Wait ~15s for first renewal cycle
PGPASSWORD=smoke psql -h 127.0.0.1 -p 55432 -U platform -d verbara \
  -c "SELECT * FROM cluster_distributed_lock;"
# Identify leader → docker stop OR docker kill → re-query
docker compose -f docker-compose.smoke-test.yml down --volumes
```

## 7. Verdict & Phase A.5 closure

Option A smoke test ✅ PASS. ADR-0022 Phase A.5 leader election is validated end-to-end against real Postgres in a production-like (modulo accelerated timings) topology.

**Phase A.5 is complete** with these acceptance items satisfied:
- SDK 2.2.1 / Pro 2.5.1-pro / Platform v2.4.2 shipped (release commits `343e9481` / `b2a60ea` / `fe8a1938`).
- 4 cosign-signed images on ghcr.io at v2.4.2 (commit `01c455f` in verbara-website authorizes the new API image digest).
- 38 new TDD tests across the three repos (13 + 25 PG-fixture-driven; full Platform Api 1721 still green).
- Native AOT publish of `Verbara.Platform.Api:v2.4.2` clean — 0 IL diagnostics, 0 managed Verbara DLLs in publish output.
- Self-contained end-to-end smoke test ✅ this document.

**What remains for Talos-lab validation against the same v2.4.2:** the lab is two minor versions behind. Plan C (`docs/plans/active/2026-05-23-lab-migration-v2.3.1-to-v2.4.2.md`) brings r55-platform up to v2.4.2 (deploys realtime micro, applies migrations, includes the gap-1 hotfix as v2.4.3). Plan B (`docs/plans/active/2026-05-23-phase-a5-talos-smoke-test.md`) runs the §6-of-original-plan smoke test against the migrated lab. Together they close C.7.

Plan A.5 will move to `docs/plans/completed/` when Plan B passes.
