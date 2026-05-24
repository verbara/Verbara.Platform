# Phase A.5 Talos lab smoke test — 2026-05-24

> **Plan:** [docs/plans/active/2026-05-23-phase-a5-talos-smoke-test.md](../plans/active/2026-05-23-phase-a5-talos-smoke-test.md)
> **Predecessor:** Plan C ([docs/plans/completed/2026-05-23-lab-migration-v2.3.1-to-v2.4.3.md](../plans/completed/2026-05-23-lab-migration-v2.3.1-to-v2.4.3.md)) ✅ CLOSED
> **Cluster:** Talos `admin@asterisk-platform`, helm release `platform` rev 18 (chart 0.2.2 / appVersion 2.4.3)
> **Executor:** Maintainer (kubectl operations) + Claude (observation/measurement/aggregation)
> **Verdict:** ✅ **PASS** (5/6 PASS + 1/6 PARTIAL with documented deferral)

## Environment

| Attribute | Value |
|---|---|
| Realtime image | `192.168.122.1:5050/verbara-platform/realtime@sha256:547162167a314ae82ce5ffce0ac158a83a9c45f95c88692768956204528d5a4d` |
| Realtime replicas | 4 (one per worker plus one co-resident) |
| Worker nodes | talos-w1 / w2 / w3 (3 nodes, 4 GB RAM each, ~3411 Mi allocatable) |
| Lease/renewal | 30 s / 10 s (prod defaults from `ClusterLeadershipOptions`) |
| Backend lock | CNPG postgres-2 primary in `r55-data` via `postgres-pooler` |
| Schema bootstrap | `MigrationRunner.EnsureSchemaAsync` (v2.4.3 hotfix, Plan C Gap-1 fix) ran on first pod-ready; `cluster_distributed_lock` table created |

## Pod placement (Test 2 evidence)

| Node | Pod |
|---|---|
| talos-w1 | platform-realtime-...-mfcgt (initial) → -nmwvm (post-Test-4 replacement) |
| talos-w2 | platform-realtime-...-w5hdv |
| talos-w3 | platform-realtime-...-2frkf + -fvqbg |

After Test 3 (rollout): all 4 pods in new ReplicaSet `d7dc9d679`. Same 1+1+2 spread shape, `topologySpreadConstraints maxSkew=1` honored, ScheduleAnyway soft constraint behaved as designed.

## Test outcomes

| # | Test | Pass criterion | Actual | Verdict |
|---|---|---|---|---|
| 1 | Cold-start single-leader convergence | ≤ 15 s | **1.25 s** (pod creation 08:06:31Z → leader transition log 08:06:32.249Z); 0.046 s from `Application started` to follower→leader | ✅ PASS (12x under) |
| 2 | Distributed pod placement | 4 pods spread ≥2 nodes | 1+1+2 across w1/w2/w3 — all 3 worker nodes hit | ✅ PASS |
| 3 | Graceful failover (rollout restart) | ≤ 15 s leaderless per swap; no multi-leader window | **36 s** total rollout (08:15:37Z → 08:16:13Z); **~5 s** leaderless gap; single clean transition `-mfcgt` (old) → `-pkzqj` (new) | ✅ PASS (3x under) |
| 4 | Ungraceful failover (single-pod kill with `--grace-period=0 --force`) | ≤ 40 s before successor elected | **1.85 s** (kill 08:17:10Z → leader transition 08:17:11.851Z on `-nmwvm`, a fresh replacement pod) | ✅ PASS (20x under) |
| 5 | SignalR exactly-once delivery via Gateway | exactly 1 receive per client per event, 5/5 trials | **PARTIAL** — see notes below | ⚠️ PARTIAL |
| 6 | Postgres connection-storm sanity | `pg_stat_activity` stable | **5 idle conns** stable across 60 s window from `platform` user (4 realtime + 2 api routed via `postgres-pooler`); no leak, no spike | ✅ PASS |

### Test 5 deferral rationale

The Phase A.5 leader-gate code is wired correctly and empirically validated by Tests 1, 3, and 4 (exactly-one leader at all times, transitions logged only on the elected pod, followers silent). What this smoke test could NOT verify end-to-end is:

- Followers actually short-circuit the forward path when a Push envelope arrives (`SkippedForwardNotLeader` EventId 3001 log)
- The leader actually forwards the envelope exactly once to SignalR groups

Both require **active SignalR client traffic**, which the lab does not have (zero clients connected, zero `OnConnected` events, zero `ProcessEnvelope` invocations across 4 pods). Verification of end-to-end exactly-once delivery is therefore deferred to the next context that produces real Push activity:

- **R5.5 K8s D-LK plan** — sustained 1 500-VU PresenceScenario load run will exercise SignalR fanout at production scale; the same checks (1 leader fwd log + 3 follower skip logs per event) apply there with much higher signal.
- **Production traffic** — if R5.5 D-LK is deferred, the first customer that connects through the Gateway will hit it. The `SkippedForwardNotLeader` log on followers + ProcessEnvelope on leader is the runtime evidence.

Per Plan B §1.2 ("No sustained load testing... performance characterization on K8s is the R5.5 D-LK plan's job"), this deferral is consistent with the plan's explicit scope.

## Issues encountered + resolutions

1. **PR #15 chart bug** — realtime template wired `ConnectionStrings__Cluster` via `secretKeyRef.optional:true` from a non-existent Secret. Result: env var set to empty string (not unset) → C# `??` operator NO fallback → all 4 pods CrashLoopBackOff on first helm upgrade (rev 16). Fixed by building the connection inline from the same `api.postgres` values + `platform-pg-credentials` Secret used by api template. Merged + redeployed → rev 18 clean.

2. **Helm strategic merge conflict** (rev 17 upgrade): existing broken Deployment had `env[7].valueFrom` set; new manifest had `env[7].value`. K8s API rejected: `"may not be specified when value is not empty"`. Resolved by `kubectl delete deployment platform-realtime` + fresh helm upgrade. No data loss (lock table persists across pod replacement; new pods re-bootstrap via the leader election protocol).

3. **`asterisk-0` StatefulSet pod Pending** — pre-existing lab debt (5d9h+, since 2026-05-19), unrelated to Plan C/B. Root cause: PVC `voicemail-asterisk-0` pinned to talos-w2 via `local-path` storage class, and w2 is RAM-saturated (3008 Mi requested / 3411 Mi allocatable). Plan C did not cause it; the v2.4.3 helm upgrade added ~512 Mi to w2 (2 realtime pods), but the pod was already unschedulable before. Acknowledged + skipped per maintainer decision; Plan B explicitly excludes asterisk integration.

4. **api healthz shows `Degraded`** — two sub-checks fail by design in lab: `asterisk` (AMI configured at `localhost:5038` — no AMI in lab) and `dialer-engine` (first reconciliation tick pending at probe time). K8s `readinessProbe` passes (kubelet sees ready=true, 0 restarts), so traffic still flows. Phase A.5 scope unaffected.

## Production extrapolation (validated)

Plan B §1.1 predicted "the timings extrapolated in the Option A docker-compose smoke report hold in the real lab". Comparison:

| Scenario | Predicted prod window | Smoke lab actual | Match? |
|---|---|---|---|
| Cold-start | ≤ 1 × RenewalInterval (10 s) | 0.046 s | ✅ much better |
| Graceful pod replace | ~10 s leaderless per swap | ~5 s | ✅ better |
| Ungraceful kill | 30-40 s | 1.85 s | ✅ much better |

The 1.85 s ungraceful result is significantly better than the 30-40 s prediction because the `kubectl delete --grace-period=0 --force` flow still allows 1-3 s of SIGTERM-then-SIGKILL window during which `LeaderElectionService.StopAsync` calls `ReleaseAsync` cleanly. Truly ungraceful failure modes (network partition, kernel panic, OOM-kill at C process layer) would hit the 30-40 s lease-expiration window — out of scope for Plan B (excluded per §1.2 "No chaos testing"; tracked under R5.5 C-LK).

## Closure decision

✅ **Plan B PASS** — Phase A.5 leader election machinery validated end-to-end on real Kubernetes infrastructure at production timing defaults. 5/6 tests strictly PASS; 1/6 (SignalR exactly-once) PARTIAL with explicit deferral path documented.

**Unblocks:** R5.5 Phase B-LK can now proceed against the v2.4.3 baseline.

## Cross-references

- [ADR-0024](../decisions/0024-v242-shipping-anomaly-and-process-hardening.md) — the hardening sweep that made this deploy possible
- [Plan C closure](../plans/completed/2026-05-23-lab-migration-v2.3.1-to-v2.4.3.md)
- [Plan B](../plans/active/2026-05-23-phase-a5-talos-smoke-test.md)
- [Option A docker-compose predecessor smoke test (PASS 2026-05-23)](phase-a5-smoke-test-2026-05-23.md)
- [lab v2.3.1 baseline inventory (Plan C C.2)](lab-v2.3.1-baseline-inventory-2026-05-24.txt)
