# Plan B — Phase A.5 Talos lab smoke test (post-v2.4.3 migration)

> **Track:** ADR-0022 Phase A.5 closure — production-fidelity validation
> **Authored:** 2026-05-23
> **Predecessor (docker-compose):** [docs/operations/phase-a5-smoke-test-2026-05-23.md](../../operations/phase-a5-smoke-test-2026-05-23.md) (Option A) — ✅ PASS
> **Original sketch (superseded):** §6 of [docs/plans/active/2026-05-22-phase-a5-cluster-leader-election.md](2026-05-22-phase-a5-cluster-leader-election.md)
> **Hard dependency:** Plan C ([docs/plans/active/2026-05-23-lab-migration-v2.3.1-to-v2.4.3.md](2026-05-23-lab-migration-v2.3.1-to-v2.4.3.md)) completed successfully.
> **Executor:** Maintainer, running against the live `admin@asterisk-platform` Talos context.

---

## 1. Goal + non-goals

### 1.1 Goal

Validate the Phase A.5 leader-election machinery (per-resource `IClusterLeader` + `LeaderElectionService` renewal loop + `PushToHubRelay` Forward-gate) end-to-end on real Kubernetes infrastructure — Talos worker nodes, CNPG-managed Postgres 18, Cilium Gateway API, Pro.Push Redis backplane — at **production timing defaults**:

| Setting | Smoke (Option A) | Talos (this plan) |
|--------|------------------|-------------------|
| `Cluster:Leadership:RenewalInterval` | 5 s | **10 s** (prod default) |
| `Cluster:Leadership:LeaseDuration` | 15 s | **30 s** (prod default) |
| Postgres backend | docker-compose `postgres:18-alpine` | CNPG primary in `r55-data` |
| Pod identity | container hostname | downward-API `POD_NAME` (`Cluster__InstanceId`) |
| Realtime replicas | 4 (`docker compose`) | 4 (`HPA min=1, max=4`) |

Prove that the timings extrapolated in [the Option A report §4 "Production extrapolation"](../../operations/phase-a5-smoke-test-2026-05-23.md#4-production-extrapolation) hold in the real lab:

| Scenario | Predicted prod window | Pass criterion in this plan |
|---------|----------------------|------------------------------|
| Cold-start single-leader convergence | ≤ 1 × RenewalInterval | ≤ 15 s (10 s loop + 5 s slack) |
| Graceful pod replace (rolling restart) | ~10 s leaderless per swap | ≤ 15 s per swap, ≤ 1 swap-window-overlap |
| Ungraceful single-pod kill | 30–40 s leaderless | ≤ 40 s before successor elected |
| SignalR exactly-once delivery via Gateway | exactly 1 receive per client per event | exactly-once, 5/5 trials |

### 1.2 Non-goals

- **No chaos testing.** No `NetworkChaos`, no CNPG primary failover, no node drain, no kernel-panic injection. Those scenarios belong to **R5.5 C-LK** (chaos-on-K8s) and are tracked by the R5.5 execution plan, not Phase A.5.
- **No sustained load testing.** Optional 5-client SignalR session for the exactly-once gate is the deliberate ceiling; the PresenceScenario's 1 500-VU REST stampede is out of scope here. Performance characterization on K8s is the [R5.5 K8s D-LK plan](2026-04-27-r5.5-execution-plan.md)'s job.
- **No Pro.Cluster routing or scheduler validation.** Phase A.5 only ships `realtime:fanout:leader`. Future resources (`dialer:campaigns:leader`, `scheduled-reports:dispatcher:leader`) are tracked via the same registration mechanism but not exercised here.
- **No cross-tenant routing fan-out tests.** Single tenant, single conversation per trial.
- **No SMB / single-pod regression.** The SMB compose stays unchanged (single Realtime pod → always-leader); covered by the existing v2.4.x compose smoke runs.

---

## 2. Pre-conditions (MUST hold before this plan starts)

All five conditions are gates; if any is red, **stop and rebase on the failed gate**.

| # | Condition | How to verify |
|---|----------|---------------|
| 1 | Plan C completed: `r55-platform` namespace on Platform `v2.4.3` with 4 Realtime replicas Ready | `helm -n r55-platform list \| grep platform` shows app version `v2.4.3`; `kubectl -n r55-platform get deploy platform-realtime -o jsonpath='{.spec.template.spec.containers[0].image}'` ends in `:v2.4.3` |
| 2 | Exactly one row in `cluster_distributed_lock` for `realtime:fanout:leader` (the lab is steady-state from Plan C's final check) | `kubectl -n r55-data exec -it r55-data-1 -- psql -U platform -d verbara -c "SELECT resource, owner, expires_at FROM cluster_distributed_lock;"` |
| 3 | Talos cluster reachable via `admin@asterisk-platform` context with all nodes Ready | `kubectl --context admin@asterisk-platform get nodes` shows 4 nodes Ready (`talos-cp1`, `talos-w1`, `talos-w2`, `talos-w3`) |
| 4 | All 38 Phase A.5 unit tests green on maintainer machine (SDK 13 + Pro 25 PG-fixture-driven) | `dotnet test` in each repo; last green run logged in the Phase A.5 plan's exit-criteria checklist |
| 5 | Option A docker-compose smoke test report exists and is PASS | [docs/operations/phase-a5-smoke-test-2026-05-23.md](../../operations/phase-a5-smoke-test-2026-05-23.md) §3 verdict line shows ✅ PASS |

Additional setup state inherited from Plan C (no action needed if Plan C completed cleanly):

- The v2.4.3-specific Gap-1 fix (`MigrationRunner.EnsureSchemaAsync` invoked at Realtime startup) is in the running image — no manual `psql` to pre-create the `cluster_distributed_lock` table.
- The chart's `realtime.cluster.instanceIdFromEnv = POD_NAME` is mapped to `Cluster__Leadership__InstanceId` (Gap-2 fix). Verify with: `kubectl -n r55-platform get pod -l app.kubernetes.io/name=platform-realtime -o jsonpath='{.items[0].spec.containers[0].env}' | jq` and look for the `Cluster__Leadership__InstanceId` key bound to `metadata.name`.
- Cilium Gateway is healthy and routing `/hubs/platform/*` to the realtime Service. Verify with `kubectl -n r55-platform get httproute realtime-hubs -o yaml` and grep for `status.parents[*].conditions[?(.type=="Accepted")].status=True`.

---

## 3. Test environment

### 3.1 Cluster & namespace

| Attribute | Value |
|-----------|-------|
| Kubeconfig context | `admin@asterisk-platform` |
| Namespace under test | `r55-platform` |
| Data namespace | `r55-data` (CNPG primary `r55-data-1`) |
| Worker nodes | `talos-w1`, `talos-w2`, `talos-w3` (4-node cluster: 1 cp + 3 workers) |
| Gateway | Cilium Gateway API; FQDN per Plan C wiring (e.g. `realtime.r55.lab.verbara.dev`) |

### 3.2 Image tags & replicas

| Component | Image | Replicas | HPA |
|-----------|-------|----------|-----|
| `platform-api` | `ghcr.io/verbara/platform/api:v2.4.3` (digest-pinned per ADR-0011) | 2 | min=1, max=2 |
| `platform-realtime` | `ghcr.io/verbara/platform/realtime:v2.4.3` (digest-pinned) | **4** | min=1, max=4 ([values.yaml lines 176–186](../../../infra/k8s/helm/platform/values.yaml)) |
| `platform-renderer` | `ghcr.io/verbara/platform/renderer:v2.4.3` | 1 | n/a |
| `platform-mail` | `ghcr.io/verbara/platform/mail:v2.4.3` | 1 | n/a |

### 3.3 Phase A.5 wiring (prod config — NO accelerated timings)

Confirm via `kubectl -n r55-platform describe pod <realtime-pod>` that the env block contains:

| Env var | Expected value | Source |
|---------|---------------|--------|
| `Cluster__Leadership__RenewalInterval` | `00:00:10` (or unset → SDK default 10 s) | chart `values.yaml` `realtime.cluster` block |
| `Cluster__Leadership__LeaseDuration` | `00:00:30` (or unset → SDK default 30 s) | chart `values.yaml` `realtime.cluster` block |
| `Cluster__Leadership__InstanceId` | `$(POD_NAME)` (resolves to e.g. `platform-realtime-7d8b9-xz4nw`) | downward API |
| `ConnectionStrings__Cluster` | CNPG primary connection string (Cluster pool key) | chart secret |
| `Cluster__LeaderResource` (read by Realtime startup) | `realtime:fanout:leader` | chart `values.yaml` line 198 |

### 3.4 Topology constraints active

From [infra/k8s/helm/platform/templates/realtime-deployment.yaml](../../../infra/k8s/helm/platform/templates/realtime-deployment.yaml) line 49 — `topologySpreadConstraints` with `maxSkew=1`, `topologyKey=kubernetes.io/hostname`, `whenUnsatisfiable=ScheduleAnyway`. With 4 pods over 3 worker nodes the expected steady-state distribution is **2-1-1** (one node hosts 2 pods, the other two host 1 each; skew = 1).

---

## 4. Test 1 — Cold-start single-leader convergence

### 4.1 Setup

The lab is already at v2.4.3 with 4 Realtime replicas Ready (Plan C's final state). To simulate cold start, scale the deployment down then back up:

```text
kubectl -n r55-platform scale deploy/platform-realtime --replicas=0
# wait ~10 s for graceful termination + lock release
kubectl -n r55-platform scale deploy/platform-realtime --replicas=4
```

### 4.2 Observation protocol

1. **Watch pods to Ready:**
   `kubectl -n r55-platform get pods -l app.kubernetes.io/name=platform-realtime -w` — wait until all 4 pods report `Ready 1/1`.
2. **Probe the lock table at T+15 s after the LAST pod reports Ready:**
   `kubectl -n r55-data exec -it r55-data-1 -- psql -U platform -d verbara -c "SELECT resource, owner, expires_at, expires_at - NOW() AS ttl_remaining FROM cluster_distributed_lock;"`
3. **Count transition log entries across all 4 pods:**
   `kubectl -n r55-platform logs -l app.kubernetes.io/name=platform-realtime --tail=200 --prefix=true | grep "Leadership transition"`

### 4.3 Pass criteria

- ✅ Lock table contains **exactly 1 row** for `realtime:fanout:leader`.
- ✅ `expires_at` is in the future (any value 0–30 s ahead is valid given the 10 s renewal cadence).
- ✅ Exactly **1** log line matches `follower -> leader, leaderInstance=<pod-name>` across the 4 pods (the other 3 stay silent — by design, see Option A report §3 Test 1).
- ✅ Lock owner matches one of the 4 pod names exactly (proves `Cluster__Leadership__InstanceId` is `POD_NAME`, not `Environment.MachineName` fallback — covers Plan C's Gap-2 fix).

### 4.4 Failure modes & first-pass diagnosis

| Symptom | Likely cause | Next step |
|--------|--------------|-----------|
| 0 rows in lock table | Migration didn't run / Realtime can't reach Postgres | Check Realtime startup logs for `MigrationRunner` lines; check `ConnectionStrings__Cluster` resolves; verify CNPG primary is `r55-data-1` |
| 2+ rows for the same resource | Schema corruption (PK violation should prevent this) | `\d cluster_distributed_lock` — verify `PRIMARY KEY (resource)`; raise a defect, halt plan |
| Lock owner is `unknown` or a hash | Downward API not injected | `kubectl -n r55-platform describe pod <p>` — verify the env block contains `Cluster__Leadership__InstanceId` from `metadata.name` |
| Multiple `follower -> leader` log entries | Election lost & re-won during boot — clock skew or DB latency | Inspect timestamps; if all within 30 s of pod start, retest after 1 min steady state; otherwise log defect |

---

## 5. Test 2 — Distributed pod placement

### 5.1 Observation protocol

`kubectl -n r55-platform get pods -l app.kubernetes.io/name=platform-realtime -o wide`

Capture the `NODE` column. Tabulate distinct nodes hosting at least one Realtime pod.

### 5.2 Pass criteria

- ✅ At least **3 distinct worker nodes** (out of the 3 available: `talos-w1`, `talos-w2`, `talos-w3`) host at least one Realtime pod.
- ✅ Distribution is **2-1-1 with skew = 1** (one worker has 2 pods, the others 1 each). Any 1-1-2 permutation is acceptable; what we reject is 4-0-0 or 3-1-0.
- ✅ Zero Realtime pods on the control-plane node `talos-cp1` (cp is tainted; this is a sanity check, not a Phase A.5 requirement).

### 5.3 Why this matters for Phase A.5

`topologySpreadConstraints` is the chart's HA guarantee. If pods are co-located, a single node loss takes 4 pods → 0 pods, and the leader-election handoff scenario in Test 4 never gets a chance to fire. This test is short but mandatory.

If the constraint is violated (`whenUnsatisfiable=ScheduleAnyway` means K8s will best-effort, not enforce), file a follow-up to harden to `DoNotSchedule` in v2.4.4 — **but do not block this plan**.

---

## 6. Test 3 — Graceful failover (rolling restart)

### 6.1 Trigger

`kubectl -n r55-platform rollout restart deploy/platform-realtime`

This issues a rolling-update with `maxUnavailable=1` (default). Each replacement:

1. K8s creates a new pod, waits for `readinessProbe` to pass (~30 s steady-state).
2. K8s sends `SIGTERM` to one old pod; that pod's `LeaderElectionService.StopAsync` runs.
3. If the dying pod was the leader, `IDistributedLock.ReleaseAsync` deletes its lock row (Phase A.5 D9, see [Option A report §3 Test 2](../../operations/phase-a5-smoke-test-2026-05-23.md#test-2--graceful-failover-docker-stop-t--0--t--4-s)).
4. Within ≤ 1 × `RenewalInterval` (10 s prod), a surviving pod's next renewal-loop iteration wins the upsert and emits `follower -> leader`.

### 6.2 Observation protocol

In one terminal, watch the lock table every second for 3 min:

```text
while true; do
  kubectl -n r55-data exec r55-data-1 -- psql -U platform -d verbara -tAc \
    "SELECT to_char(NOW(),'HH24:MI:SS') || ' owner=' || COALESCE(owner,'<NONE>') || ' ttl=' || COALESCE(EXTRACT(EPOCH FROM expires_at - NOW())::text,'-') FROM cluster_distributed_lock RIGHT JOIN (SELECT 1) z ON true;"
  sleep 1
done
```

In another terminal:

```text
kubectl -n r55-platform logs -l app.kubernetes.io/name=platform-realtime --tail=0 -f --prefix=true | grep "Leadership transition"
```

In a third terminal, kick off the restart:

```text
kubectl -n r55-platform rollout restart deploy/platform-realtime
kubectl -n r55-platform rollout status deploy/platform-realtime --watch
```

### 6.3 Pass criteria

- ✅ **Total rollout wall-clock:** ~2 min for 4 pods × ~30 s readiness with `maxUnavailable=1`.
- ✅ **Per-swap leaderless gap:** ≤ 15 s (10 s renewal interval + 5 s slack for the surviving pod's renewal-loop alignment).
- ✅ Across the entire rollout, the lock table is either:
  - Owned by exactly one pod (steady-state windows), OR
  - Empty (transient ~10 s windows when the leader's `Release` already deleted the row and no surviving pod has renewed yet).
  Never 2+ rows.
- ✅ Across the entire rollout, the leader-transition log shows a clean sequence of `leader -> released` (from each dying leader) interleaved with `follower -> leader` (from each successor). No `follower -> leader` overlaps another pod's `leader` ownership (visual inspection of timestamps).
- ✅ Final state matches Test 1's steady state: 4 pods Ready, 1 lock row, 1 leader.

### 6.4 Note on rolling-restart leader-distribution

Rolling restart does NOT guarantee the leader is the LAST pod swapped. If pod-0 is the initial leader and K8s picks pod-0 first to replace, leadership transfers to one of pods 1/2/3, then later pod-1 or pod-2 may become leader, then the final state may have ANY of the 4 new pods as leader. This is correct behavior — the test asserts leaderless-gap bounds, not leader pod identity.

---

## 7. Test 4 — Ungraceful failover (single-pod kill)

### 7.1 Identify the current leader

`kubectl -n r55-data exec r55-data-1 -- psql -U platform -d verbara -tAc "SELECT owner FROM cluster_distributed_lock WHERE resource='realtime:fanout:leader';"` → returns e.g. `platform-realtime-7d8b9-xz4nw`.

### 7.2 Trigger

`kubectl -n r55-platform delete pod platform-realtime-7d8b9-xz4nw --force --grace-period=0`

`--force --grace-period=0` skips `preStop` and `SIGTERM`. The pod is removed from the apiserver immediately; the container is killed with `SIGKILL`. `LeaderElectionService.StopAsync` does NOT run. `ReleaseAsync` is NOT called. The lock row survives in Postgres with the dead pod's owner ID until `expires_at < NOW()`.

### 7.3 Observation protocol

Same two terminals as Test 3 (lock-table polling + transition log tail). Start them BEFORE the delete.

Note the wall-clock at the moment the delete returns (T = 0). Capture:

- T at which the lock-row's `ttl_remaining` reaches 0 (expected: ≤ 30 s, since the dead pod's last renewal was ≤ 10 s before death).
- T at which a surviving pod logs `follower -> leader`.
- T at which the lock table shows a new owner (different from the killed pod).

### 7.4 Pass criteria

- ✅ **Successor elected within `LeaseDuration + RenewalInterval` = 30 + 10 = 40 s** from T = 0. (Option A measured 14 s with 15 s + 5 s smoke timings; production scales to 40 s ceiling per [Option A §4](../../operations/phase-a5-smoke-test-2026-05-23.md#4-production-extrapolation).)
- ✅ During the leaderless window (T = 0 → T_successor), the lock table shows the dead pod as owner (this is the EXPECTED behavior — the row only updates on the next acquire-or-renew cycle by a live pod). No other pod simultaneously claims leadership.
- ✅ Exactly one surviving pod logs `follower -> leader, leaderInstance=<surviving-pod-name>`. No other surviving pod emits the same log within ±2 s of that timestamp.
- ✅ Pod-kill does NOT cascade: the other 3 surviving pods stay Ready, no restart loops, no crash. `kubectl -n r55-platform get pods -l app.kubernetes.io/name=platform-realtime` shows 4 pods Ready within 30 s of the delete (K8s recreates the killed pod automatically; that newcomer joins as follower).
- ✅ Zero `Failed to renew leadership` warnings on surviving pods during the leaderless window. (The rejection of a renewal attempt while the dead lease is still in-window is NOT an exception; followers should silently return `false` from `TryAcquireAsync` and continue their loop.)

### 7.5 Why ≤ 40 s (and not ≤ 30 s)

The 30 s is the `LeaseDuration`. The dead leader's LAST renewal could have been up to 10 s before death (`RenewalInterval`). So `expires_at` ≤ death + 30 s + 10 s = death + 40 s in the worst case. Add up to 1 × `RenewalInterval` for a surviving pod's next loop iteration to hit the expired row → real-world ceiling 40 + 10 = 50 s, with mean ~25–30 s. The pass criterion is the deterministic ceiling (40 s before any pod CAN re-acquire); add 10 s pad and accept up to 50 s in practice while still recording the measured value.

---

## 8. Test 5 — SignalR exactly-once delivery (the gate test)

> **This is the most important test in the plan.** It proves the `PushToHubRelay` Forward-gate works end-to-end with 4 active SignalR backplane subscribers (one per pod) plus 5 real SignalR clients connected via the production gateway, and that the gate prevents duplicate fanout.

### 8.1 Setup — 5 SignalR clients via Cilium Gateway

The [PresenceScenario](../../../tests/Verbara.Platform.LoadTests/Scenarios/PresenceScenario.cs) currently drives REST traffic, not SignalR (as documented in its own header comment: *"True presence-fanout measurement requires a dedicated SignalR client load tool — Phase C-L follow-up, not Phase B-L HTTP."*).

For Phase A.5, the maintainer connects 5 SignalR clients using the official @microsoft/signalr browser client OR the .NET `HubConnectionBuilder` against the gateway URL:

- Hub URL: `https://<realtime-gateway-host>/hubs/platform` (matches [Realtime Program.cs line 199](../../../src/Verbara.Platform.Realtime/Program.cs) → `app.MapHub<PlatformHub>("/hubs/platform")`).
- Auth: a valid JWT issued by `platform-api` for the loadtest tenant (`X-Tenant-Id: loadtest`).
- After `HubConnection.StartAsync`, each client invokes `JoinTenantGroup(tenantId)` (or whichever the current PlatformHub method is — check `src/Verbara.Sdk.Pro.Push.SignalR/Hubs/PlatformHub.cs` for the canonical method name).
- Each client registers a handler for `ConversationStateChanged` and pushes the received event payload into a per-client list with a receive timestamp.

Each of the 5 clients is given a unique client ID (`Client-A` … `Client-E`). The Cilium Gateway distributes the 5 WebSocket connections across the 4 realtime pods roughly evenly (4 or 5 connections → 4 pods, so at least one pod owns ≥ 2 connections; this is the multi-connection-per-pod stress point).

### 8.2 Trigger a state change via Platform.Api REST

Pick a single existing conversation in the loadtest tenant. Trigger a state transition that publishes a `ConversationStateChangedEvent` to `IPushEventBus`. Candidate endpoints from [src/Verbara.Platform.Api/Endpoints/ConversationEndpoints.cs](../../../src/Verbara.Platform.Api/Endpoints/ConversationEndpoints.cs):

| Endpoint | State emitted | Notes |
|----------|---------------|-------|
| `POST /api/v1/conversations/{id}/accept` | `Active` | Requires assigned agent context |
| `POST /api/v1/conversations/{id}/hold` | `OnHold` | Safest reversible trigger |
| `POST /api/v1/conversations/{id}/unhold` | `Active` | Pair with `hold` for repeat trials |
| `POST /api/v1/conversations/{id}/close` | `Closed` | Terminal; not reusable in same conversation |

**Recommended:** alternate `hold` → `unhold` → `hold` → ... so the same conversation can drive all 5 trials. Use `curl` against the gateway-fronted Api FQDN with a tenant-admin JWT.

> **No `/api/v1/internal/push/test` endpoint exists in v2.4.3.** The user's prompt mentioned it as a candidate — confirmed by grep that it is absent. Use the conversation-state endpoints above. If a dedicated test-only push endpoint is desired for future smoke runs, file a v2.5.x backlog item.

### 8.3 Observation protocol

For each trial (5 trials total):

1. Reset each of the 5 clients' received-events list to empty.
2. Fire ONE state-change REST call (200 OK from Api).
3. Wait 3 s for SignalR delivery to settle.
4. For each of the 5 clients, assert `receivedEvents.Count == 1`. Capture the actual count if non-1.
5. Capture which pod was leader at the moment of the trigger (`kubectl -n r55-data exec r55-data-1 -- psql ... SELECT owner ...`) for the trial log.

### 8.4 Pass criteria

- ✅ For each of 5 trials × 5 clients = 25 (trial, client) cells, the received-events count is **exactly 1**. Any 0 (lost event) or 2+ (duplicate fanout) fails the gate.
- ✅ Across the 5 trials, the leader pod can rotate or stay constant — both outcomes are valid. The test does NOT assert leader-pinning during the 5 trials.
- ✅ Event payload integrity: the `conversationId`, `oldState`, `newState` fields in the received payload match the REST call's expected emission (cross-check with the Api response).

### 8.5 Why this test gates the entire plan

The Forward-gate in [PushToHubRelay.cs](../../../src/Verbara.Platform.Realtime/Services/PushToHubRelay.cs) lines 53–70 is the *only* application-layer mechanism preventing 4× duplicate SignalR delivery. Unit tests pin the gate behavior with a `FakeClusterLeader` (4 `PushToHubRelayTests`), but the unit tests don't exercise:

- The real SignalR Redis backplane fanout between pods.
- The real Cilium Gateway WebSocket sticky-session behavior (or absence thereof).
- The real timing relationship between an in-flight event and a concurrent leader-handoff (none expected in steady state, but the test catches regressions).

If this test fails, **Phase A.5 is not production-ready** regardless of Tests 1–4 passing.

### 8.6 Failure-mode triage

| Observation | Likely cause | Next step |
|-------------|--------------|-----------|
| Client receives 0 events | Group-join failed / hub-method mismatch / leader-gate too aggressive | Inspect Realtime logs for `[RELAY] Forwarded` line; if absent on the leader pod, gate is wrong; if present but no client receives, group routing wrong |
| Client receives 2 events | Two pods believe they are leader (split-brain) OR backplane echo bug | Snapshot lock table immediately; check all 4 pods' last `IsLeader` log; if 2 pods claim leader, the SQL atomic-upsert invariant is violated → halt plan, raise defect |
| Client receives 4 events | Forward-gate disabled / `IClusterLeader` not injected / `IsLeader` always true | Check Realtime startup logs for the leader-resource registration; check the relay constructor for the `[FromKeyedServices]` attribute presence in the running image |
| Only some clients receive 1 event, others 0 | Group-routing or sticky-session issue (not Phase A.5) | Track separately; this is a Pro.Push concern, not a leader-gate concern. Do NOT fail Phase A.5 on this — file a Pro.Push defect and re-run after fix. |

---

## 9. Test 6 — Postgres connection-storm sanity

### 9.1 Goal

Confirm the leader-election loop adds **at most 1 sustained extra connection per Realtime pod** to the CNPG primary. With 4 pods × 1 query / 10 s = 0.4 qps aggregate, this should be invisible in `pg_stat_activity`.

### 9.2 Observation protocol

```text
kubectl -n r55-data exec r55-data-1 -- psql -U platform -d verbara -c \
  "SELECT application_name, state, count(*)
   FROM pg_stat_activity
   WHERE application_name LIKE '%Realtime%' OR application_name LIKE '%platform%'
   GROUP BY application_name, state ORDER BY 1, 2;"
```

Capture once during steady state. Then snapshot again 30 s later and verify connection counts are stable (no leak).

### 9.3 Pass criteria

- ✅ Steady-state connection count from `Verbara.Platform.Realtime` to the `Cluster` pool ≤ `4 pods × ConnectionPoolMaxSize` (per the per-pool sizing from ADR-0015 Phase 2). The exact ceiling depends on Plan C's pool-config snapshot; the figure to alert on is "more than 2× baseline before Phase A.5" not an absolute number.
- ✅ Zero `LOG: too many connections` or `FATAL` messages in CNPG primary logs (`kubectl -n r55-data logs r55-data-1 --tail=500 | grep -i "too many\|FATAL"`).
- ✅ No measurable change in p99 query latency for the lock-acquire SQL between Test 1's start and Test 6's snapshot — implicitly validated by Tests 3 + 4 not exceeding their failover-time budgets.

### 9.4 If this fails

A connection leak in `PostgresDistributedLock` would manifest as a monotonically growing connection count on the `Cluster` pool. This is the kind of bug the 13 SDK PG-fixture tests + 25 Pro PG-fixture tests should have caught — if it appears here, file a defect against `Verbara.Sdk.Cluster.Postgres` and bisect. Halt the plan.

---

## 10. Pass criteria summary

Fill in the "Measured" + "Verdict" columns during the test run.

| # | Test | Expected | Measured | Verdict |
|---|------|----------|----------|---------|
| 1 | Cold-start single leader | 1 lock row, 1 transition log | _____ | _____ |
| 2 | Distributed pod placement | ≥ 3 worker nodes, skew ≤ 1 | _____ | _____ |
| 3 | Graceful rolling restart | ≤ 15 s leaderless per swap, no overlap | _____ | _____ |
| 4 | Ungraceful single-pod kill | ≤ 40 s leaderless, no double-leader | _____ | _____ |
| 5 | SignalR exactly-once | 25/25 cells = 1 receive | _____ | _____ |
| 6 | PG connection storm sanity | ≤ 2× baseline conns; 0 FATALs | _____ | _____ |

**Overall verdict:** ☐ ✅ PASS — all 6 tests green ☐ ⚠️ PARTIAL — Tests 1–5 green, 6 with caveat ☐ ❌ FAIL — at least one of Tests 1–5 red

The smoke test PASSES if Tests 1–5 are green AND Test 6 is ≤ PARTIAL with documented caveats.

---

## 11. Rollback path

If any of Tests 1–5 fails:

1. **Snapshot state first.** Before rolling back, capture:
   - Full Realtime pod logs: `kubectl -n r55-platform logs -l app.kubernetes.io/name=platform-realtime --prefix=true --tail=2000 > /tmp/phase-a5-failure-realtime.log`
   - Lock-table snapshot at failure: `kubectl -n r55-data exec r55-data-1 -- psql -U platform -d verbara -c "SELECT * FROM cluster_distributed_lock;"`
   - Pod placement: `kubectl -n r55-platform get pods -l app.kubernetes.io/name=platform-realtime -o wide`
2. **Roll back the chart:**
   `helm -n r55-platform rollback platform 0` (rolls to the previous revision — should be v2.4.2 if Plan C went from v2.3.1 → v2.4.3 in two helm releases, otherwise inspect `helm -n r55-platform history platform` first).
3. **Confirm rollback:** all pods on `:v2.4.2`, Realtime down to single replica per pre-Phase-A.5 baseline; `cluster_distributed_lock` table either drops or stays empty (lock unused at v2.4.2 since the Forward-gate isn't shipped).
4. **Root-cause the v2.4.3-specific issue.** Document in a defect report under `docs/research/` referencing this plan + the failed test ID + the captured artifacts.
5. **Ship v2.4.4 with the fix.** Reuse the Plan C migration runbook (or just `helm upgrade` if the change is chart-only).
6. **Re-run this plan from §4.** No need to re-run Plan C if v2.4.3 → v2.4.4 is a forward-compatible patch.

If Test 6 alone fails (connection leak) but Tests 1–5 are green: ship v2.4.4 hotfix on a non-blocking timeline, do NOT roll back. The leader-election machinery is correct; only its resource usage needs tightening.

---

## 12. Acceptance criteria for closing Phase A.5

Phase A.5 is FULLY CLOSED when ALL of the following are checked:

- [ ] **Smoke-test run successful.** Tests 1–5 ✅ PASS, Test 6 ≤ ⚠️ PARTIAL with no blocking caveat.
- [ ] **Smoke-test report written:** append a §8 "Talos lab smoke test" section to [docs/operations/phase-a5-smoke-test-2026-05-23.md](../../operations/phase-a5-smoke-test-2026-05-23.md) capturing measurements per §10 above, OR (preferred) author a separate `docs/operations/phase-a5-talos-smoke-test-2026-05-23.md` and cross-link both reports.
- [ ] **§3 risk-row update applied:** the Phase A.5 plan's risk table currently states "Leaderless window during election (~10 s)" — update to "~10 s graceful / ~30–40 s ungraceful" per the Option A report Gap 3 + this plan's Test 4 measurement. Edit the entry in [docs/plans/active/2026-05-22-phase-a5-cluster-leader-election.md](2026-05-22-phase-a5-cluster-leader-election.md) §8 before moving the plan.
- [ ] **Phase A.5 plan moved to `completed/`:**
  `git mv docs/plans/active/2026-05-22-phase-a5-cluster-leader-election.md docs/plans/completed/`
- [ ] **This Plan B moved to `completed/`:**
  `git mv docs/plans/active/2026-05-23-phase-a5-talos-smoke-test.md docs/plans/completed/`
- [ ] **Plan C moved to `completed/`** (if Plan C didn't self-close):
  `git mv docs/plans/active/2026-05-23-lab-migration-v2.3.1-to-v2.4.3.md docs/plans/completed/`
- [ ] **Memory updates** (`~/.claude/projects/-media-Data-Source-Verbara-Verbara-Platform/memory/MEMORY.md`):
  - Move the `2026-05-22-phase-a5-cluster-leader-election.md` bullet from "Active Plans" to "Recently Completed Plans".
  - Add a "Phase A.5 — Talos lab smoke test PASS" bullet under "Major Trains".
  - Update the "Current state" / `project_current_position.md` snapshot to mark **ADR-0022 track FULLY CLOSED with both Option A + Option B smoke tests PASS**.
  - Add a memo file `project_phase_a5_talos_smoke_pass.md` capturing the §10 measurement table for future reference.
- [ ] **Roadmap update** (`project_roadmap.md`): strike "Phase A.5 (Pro.Cluster IsLeader)" from the remaining work; ADR-0022 track is now empty.

When all boxes are checked, commit with:

```text
docs(plans): close ADR-0022 Phase A.5 — Talos lab smoke test PASS (v2.4.3)
```

(Conventional Commit, English content, no `Co-Authored-By`.)

---

## 13. Out of scope / forward-looking items

Items deliberately NOT exercised by this plan, listed so the maintainer doesn't conflate them with Phase A.5 closure:

- **Pro.Cluster routing leader-awareness.** `Router` does not consult `IsLeader` at v2.4.3. Tracked by the Phase A.5 plan §10 "Out of scope" list; revisit when Dialer or scheduled-reports port to leader-election.
- **`OnLeadershipChanged` event-driven hook.** Polling at the relay forward boundary is sufficient at v2.4.3. Revisit if a consumer needs sub-10s reaction.
- **Fencing tokens (`LeaderEpoch`).** Reserved for Dialer's outbound-campaign leader-gating. Phase A.5 ships without.
- **R5.5 C-LK chaos protocol (NetworkChaos, PG primary failover, node drain).** Tracked separately. Phase A.5 smoke test specifically does NOT chaos-test; the deterministic single-pod-kill in Test 4 is the only adversarial action.
- **R5.5 D-LK sustained 24h soak on K8s with Realtime multi-pod.** Bundled with the Pro v2.5.0-pro train scheduling (calendar-gated, eligible post-2026-06-28). Phase A.5 leaves the lab in a state that's ready to feed into D-LK; D-LK itself is its own plan.
- **CSAT consumer (Platform v2.4.0 paused work).** Independent track; not Phase A.5 material.

---

## 14. Open questions for the maintainer

1. **Hub group-join method name.** §8.1 of this plan says "each client invokes `JoinTenantGroup(tenantId)`" but the actual method name on `PlatformHub` at v2.4.3 should be confirmed against [src/Verbara.Sdk.Pro.Push.SignalR/Hubs/PlatformHub.cs](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/src/Verbara.Sdk.Pro.Push.SignalR/Hubs/PlatformHub.cs) (closed-source repo) before the test run. If the method is `SubscribeTenant` or `Join` or auto-joined-on-connect, adjust the client wiring accordingly.
2. **Realtime gateway hostname.** Plan C should publish the exact FQDN; if it isn't surfaced explicitly, derive from `kubectl -n r55-platform get httproute realtime-hubs -o jsonpath='{.spec.hostnames[0]}'` before §8.1.
3. **Connection-pool baseline for Test 6.** ADR-0015 Phase 2 single-pool sizing depends on the specific tier — confirm against Plan C's pool-config snapshot whether the `Cluster` pool key shares with the main `Postgres` pool or is split. If split, document the expected idle-connection floor.
4. **CNPG primary identity stability.** This plan assumes `r55-data-1` is the steady-state primary. If CNPG has failover-promoted to `r55-data-2` or `r55-data-3` between Plan C completion and this plan's run, substitute the current primary in the `kubectl exec` commands; the plan logic is unchanged.
5. **PresenceScenario SignalR rewrite — opt-in or skip?** Whether to actually implement the .NET-based 5-client SignalR harness as a one-off script vs reuse an existing dev script is a judgment call. The plan only requires that 5 SignalR clients connect via the gateway and assert exactly-once delivery; the harness implementation is owner's discretion.

---

## 15. References

- ADR-0022 — Platform API AOT shipping path → [docs/decisions/0022-platform-api-aot-shipping-path.md](../../decisions/0022-platform-api-aot-shipping-path.md)
- Phase A.5 plan (predecessor) → [docs/plans/active/2026-05-22-phase-a5-cluster-leader-election.md](2026-05-22-phase-a5-cluster-leader-election.md)
- Option A docker-compose smoke test → [docs/operations/phase-a5-smoke-test-2026-05-23.md](../../operations/phase-a5-smoke-test-2026-05-23.md)
- Plan C lab migration → [docs/plans/active/2026-05-23-lab-migration-v2.3.1-to-v2.4.3.md](2026-05-23-lab-migration-v2.3.1-to-v2.4.3.md)
- Realtime deployment template → [infra/k8s/helm/platform/templates/realtime-deployment.yaml](../../../infra/k8s/helm/platform/templates/realtime-deployment.yaml)
- Realtime Helm values (HPA + cluster block) → [infra/k8s/helm/platform/values.yaml](../../../infra/k8s/helm/platform/values.yaml) lines 155–206
- Forward-gate implementation → [src/Verbara.Platform.Realtime/Services/PushToHubRelay.cs](../../../src/Verbara.Platform.Realtime/Services/PushToHubRelay.cs)
- Leader-resource constants → [src/Verbara.Platform.Realtime/Services/RealtimeLeaderResources.cs](../../../src/Verbara.Platform.Realtime/Services/RealtimeLeaderResources.cs)
- Realtime hub mount path (`/hubs/platform`) → [src/Verbara.Platform.Realtime/Program.cs](../../../src/Verbara.Platform.Realtime/Program.cs) line 199
- Conversation state-change endpoints → [src/Verbara.Platform.Api/Endpoints/ConversationEndpoints.cs](../../../src/Verbara.Platform.Api/Endpoints/ConversationEndpoints.cs) lines 22–30
- LoadTests scenario reference (PresenceScenario, currently REST not SignalR) → [tests/Verbara.Platform.LoadTests/Scenarios/PresenceScenario.cs](../../../tests/Verbara.Platform.LoadTests/Scenarios/PresenceScenario.cs)
- R5.5 execution plan (chaos + soak follow-ups; OUT OF SCOPE for Phase A.5) → [docs/plans/active/2026-04-27-r5.5-execution-plan.md](2026-04-27-r5.5-execution-plan.md)
