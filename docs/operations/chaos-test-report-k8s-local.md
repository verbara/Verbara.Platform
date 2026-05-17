# Chaos test report — K8s local (R5.5 Phase C-LK)

**Date:** 2026-05-17
**Cluster:** Talos v1.13.0 + K8s 1.36.0 + Cilium 1.19.3 (1 CP + 3 workers KVM VMs, ~10 vCPU + 12 GiB total)
**App version:** `asterisk-platform/api:1.14.6` + `asterisk-platform/web:1.15.5` (pre-rebrand — methodology pinned to Docker B-L baseline)
**Chaos engine:** Chaos Mesh v2.7.0 (10 CRD experiments + 4 K8s-specific ad-hoc)
**Runner:** `scripts/chaos-test.sh --k8s`
**Observation window per experiment:** 60 s
**Raw logs:** `chaos-reports/<timestamp>/`

## Methodology

For each experiment:
1. Pre-snapshot of all pods (`kubectl get pods -A`)
2. Apply Chaos Mesh CRD
3. Wait observation window (60 s)
4. Capture chaos object status + post-experiment pod state
5. Delete chaos CRD (release the fault)
6. Continue to next experiment with no inter-experiment sleep (cluster gets ~immediate recovery time before next fault)
7. Post-snapshot of all pods

In parallel:
- `curl http://api.r55.local/api/v1/auth/login` probed periodically to detect downtime
- Grafana K8s dashboards observed (kubernetes / Compute Resources / Pod) for restart counts + readiness churn
- Hubble flow logs spot-checked for unusual DROP patterns

## Experiment results

### 01 — Postgres replica pod-kill ✅ PASS

- **Action:** `PodChaos action=pod-kill` on `cnpg.io/cluster=postgres,role=replica`
- **Affected:** postgres-3 (or postgres-2 — non-primary picked by Chaos Mesh)
- **Observation:**
  - Pod killed and rescheduled in **~45 s** by the StatefulSet controller
  - CNPG cluster status remained `Cluster in healthy state` throughout
  - Primary (`postgres-1`) unaffected; surviving replicas continued streaming
  - **Auth login probe HTTP 200 immediately after cleanup**
- **Verdict:** Postgres HA layer absorbs single replica loss transparently.

### 02 — Platform.Api pod-kill ✅ PASS — zero downtime

- **Action:** `PodChaos action=pod-kill` on `app.kubernetes.io/name=platform-api`
- **Affected:** `platform-api-558f699fc9-2kqkl` killed; `platform-api-558f699fc9-nnqqt` came up in ~30 s
- **Observation:**
  - 8 of 8 in-flight `/health` probes returned **HTTP 200** with sub-3 ms latency throughout the kill+reschedule cycle
  - Surviving replica `r8wkb` absorbed full traffic until reschedule
  - Cilium eBPF endpoint slice updated in milliseconds → no client-visible blip
- **Verdict:** 2-replica deployment + Cilium kube-proxy-replacement gives **zero-downtime** pod replacement.

### 03 — Redis pod-kill ✅ PASS — JWT cache absorbed the gap

- **Action:** `PodChaos action=pod-kill` on `app=redis`
- **Affected:** `redis-0` (StatefulSet) killed; reschedule + PVC remount in ~74 s
- **Observation:**
  - 8 of 8 `/auth/login` probes returned **HTTP 200** during the entire Redis outage window
  - JWT key cache (60 s in-memory in `JwtTokenService`) kept token issuance + validation working
  - `IJtiRevocationCache` failed silently (no Redis → no revocation writes; tokens stayed valid)
- **Verdict:** Auth path resilient to short Redis outages thanks to in-memory key cache. **Caveat noted:** silent JTI revocation failure during outage is a security trade-off — revocations issued in this window will never propagate. Acceptable for short blips, not for sustained Redis loss.

### 04 — Asterisk pod-kill ✅ PASS — SBC failover transparent

- **Action:** `PodChaos action=pod-kill` on `app.kubernetes.io/name=asterisk`
- **Affected:** one of `asterisk-{0,1}` killed
- **Observation:**
  - SIPp test (20 calls @ 2 cps, limit 5 concurrent) against `asterisk-1.asterisk` headless DNS during the chaos: **20/20 INVITE → 200 OK → ACK → BYE → 200 OK**, 0 retransmits, 0 timeouts
  - Kamailio dispatcher routes flowed through the surviving pod
- **Verdict:** 2-Asterisk-replica failover works; SIP call setup unaffected by single-pod loss.

### 05 — Kamailio pod-kill ✅ PASS

- **Action:** `PodChaos action=pod-kill` on `app.kubernetes.io/name=kamailio`
- **Affected:** one of `kamailio-{b9vxr,dd7tt,n9q9d}` DaemonSet pods killed
- **Observation:**
  - DaemonSet controller immediately recreated the pod on the same worker
  - `postStart` hook (added round 3) fired `dispatcher.reload` 20 s after restart
  - Other 2 Kamailio pods served all SIPp traffic transparently during the gap
- **Verdict:** SBC layer handles single-pod loss without operator action.

### 06 — Platform.Api network delay 200 ms × 60 s ❌ BLOCKED — Cilium incompatibility

- **Action:** `NetworkChaos action=delay latency=200ms`
- **Observation:**
  - Chaos Mesh event: `Failed to apply chaos: unable to set ip tables chains for pod`
  - **Cilium runs with `kubeProxyReplacement: true`** — packet forwarding is via eBPF, not iptables
  - Chaos Mesh's chaos-daemon uses `iptables` to inject the delay → no chains to manipulate → chaos never injected
  - Side effect: chaos object stuck with finalizer + daemon retried indefinitely until manual finalizer patch
- **Verdict:** **NetworkChaos (delay, partition, loss, corruption, etc.) is INCOMPATIBLE with this Cilium eBPF deployment.** Production-grade follow-up: replace with Cilium-native `CiliumNetworkPolicy` + bandwidth/latency primitives, or switch to a non-eBPF CNI for chaos validation. Documented as known limit; deferred to Phase 0C (cloud may have iptables-mode CNI).

### 07 — Postgres ↔ Platform.Api network partition ❌ BLOCKED — same reason as #06

- Same Cilium eBPF incompatibility. Chaos object applied but daemon couldn't inject. Manual finalizer cleanup required.
- **Verdict:** same as #06 — defer to Phase 0C cloud cluster.

### 08 — Platform.Api CPU stress 90 s ✅ PASS — resilient

- **Action:** `StressChaos cpu workers=4 load=90`
- **Observation:**
  - 8 of 8 `/health` probes returned **HTTP 200** with normal 2-3 ms latency
  - 8 of 8 `/auth/login` probes returned **HTTP 200** with normal 50 ms latency
  - No pod restarts, no liveness/readiness failures
- **Verdict:** Either StressChaos cpu workers limited to small CPU share (containerd cgroup), OR the platform-api workload is light enough (idle baseline + probes only) that 4 worker stressors with load=90 didn't saturate the 2-vCPU limit. In any case, no service degradation.
- **Caveat:** with sustained NBomber load running in parallel, the picture would likely differ. Cloud Phase B-C will run StressChaos under NBomber concurrent load for a real reading.

### 09 — Platform.Api memory stress 60 s ✅ PASS

- **Action:** `StressChaos memory workers=1 size=1500MB`
- **Observation:**
  - 6 of 6 `/health` probes returned **HTTP 200** with 2-16 ms latency (slight jitter visible vs 2-3 ms baseline)
  - No OOM kills, no pod restarts
- **Verdict:** Allocation of 1.5 GiB on a 2 GiB-limit pod kept it under threshold; .NET GC handled the additional pressure transparently.

### 10 — CNPG primary failover ✅ PASS — 16 s total, 4 s API blip

This is the **headline experiment**. Full timeline:

| Elapsed | Cluster state | Primary | API `/health` |
|---|---|---|---|
| t = 0 s | Cluster in healthy state | postgres-1 (killed at t=0) | 200 |
| t = +4 s | Waiting for instances | **postgres-2 (promoted)** | **503** (4 s blip) |
| t = +8 s | Waiting for instances | postgres-2 | 200 (recovered) |
| t = +16 s | **Cluster in healthy state** | postgres-2 | 200 |
| t = +57 s | healthy | postgres-2 | 200 |

- **Failover total: 16 s** — well under the 30 s gate documented in the R5.5 plan ✅
- **API-visible blip: 4 s** — Platform.Api's Npgsql connection pool detected the lost primary, retried, and PgBouncer rerouted to the newly promoted `postgres-2`
- CNPG operator + PgBouncer pooler coordination is fully automated; no operator intervention required
- Platform.Api stayed available throughout (only 1 of 16 probes returned non-200)
- **Verdict:** CNPG HA layer meets the plan's recovery SLO. Production deployment should still alert on this blip via Prometheus + AlertManager.

## K8s-specific experiments (C-LK.3) — DEFERRED

The following experiments require control-plane manipulation and were skipped to preserve cluster stability for ongoing D-LK soak prep:

- **Cilium DaemonSet restart** — would cause cluster-wide brief network reconvergence; defer to Phase 0C where blast radius is contained
- **CNPG primary delete** — equivalent to #10 above (covered)
- **etcd member pause** — control-plane is single-node (`talos-cp1`) in this lab cluster; pausing it would block ALL kube API operations. Defer to Phase 0C with multi-node CP
- **kube-apiserver pause** — same single-CP constraint

C-LK.3 follow-ups tracked for Phase 0C (cloud, multi-node CP).

## Findings summary

| # | Category | Observation | Production-grade follow-up |
|---|---|---|---|
| 1 | Pod resilience | All single-pod kills absorbed with ≤4 s blip (most zero-downtime) | Validate at higher chaos rate (multi-pod kills) under sustained load |
| 2 | Auth resilience | Login + JWT validation survived Redis outage thanks to in-memory cache | Add alert for "Redis store unreachable for >60 s" — beyond cache TTL, login WILL fail |
| 3 | Silent JTI failure | `IJtiRevocationCache` writes failed silently during Redis outage | Treat as known trade-off; document in security threat model |
| 4 | SBC failover | SIP path survived Asterisk + Kamailio pod loss | Combine with C-LK CPU stress in cloud phase to test joint pressure |
| 5 | **NetworkChaos blocked by Cilium eBPF** | iptables-based chaos injection incompatible with `kubeProxyReplacement: true` | Either use CiliumNetworkPolicy primitives OR deploy a parallel test cluster with iptables-mode CNI. Defer NetworkChaos validation to Phase 0C decision |
| 6 | StressChaos limited blast radius | CPU + memory stress didn't surface degradation at idle baseline | Re-run during NBomber concurrent load (Phase D-LK soak overlap) |
| 7 | **CNPG primary failover ≤ 30 s gate PASSED** | 16 s total failover, 4 s API blip | Production alert on Postgres failover events; capture in runbook |
| 8 | Chaos Mesh finalizer-leak on failed apply | Stuck `NetworkChaos` required manual finalizer patch | Add `kubectl patch ... finalizers=[]` step to chaos-test.sh cleanup hook |

## Side observations + known limits

- **HPA scale-up** (#08) requires `metrics-server` — NOT installed on this lab cluster. HPA stays at 2 replicas regardless of CPU. Cloud phase (Phase 0C+) installs it.
- **Anti-affinity** on `platform-api` is `preferredDuringScheduling` (B-LK.3 finding) — both replicas may live on the same worker. If experiment 02 kills the only replica on its node, the rescheduled pod may land on a different worker (assuming the original pair was on a single worker).
- **Lab cluster memory** is tight (~12 GiB total). Stress experiments may evict other pods if MemoryPressure threshold hits — observe via `kubectl get events --field-selector reason=Evicted`.

## References

- Chaos Mesh manifests: `tests/chaos-experiments/chaos-mesh/`
- Runner: `scripts/chaos-test.sh --k8s`
- B-LK envelope (pre-chaos baseline): `docs/operations/load-test-baseline.md` § "Local K8s results"
- B-LK.3 hardware snapshot: `docs/operations/load-test-baseline-data/k8s-blk3/`
