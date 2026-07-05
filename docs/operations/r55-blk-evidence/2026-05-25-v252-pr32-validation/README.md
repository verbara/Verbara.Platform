# R5.5 Phase B-LK · PR #32 validation closure — Platform v2.5.2 on Talos lab

**Date:** 2026-05-25 01:01–01:04 UTC
**Target:** `http://api.r55.local` via Cilium Gateway → `r55-platform/platform-api:5000`
**Image:** `ghcr.io/verbara/platform/api:v2.5.2` (manifest digest `sha256:0e8cc50d3b4c1ef4643d7b1709a4636b6fa0a7d3b664009bbe74c4cd25d8ef1c`)
**Lab state:** helm release `platform` revision 26, chart `platform-0.2.10`, 2 platform-api + 4 platform-realtime + 1 web

## Purpose

Validate end-to-end that the K8s liveness/readiness contract fix shipped in
[PR #32](https://github.com/verbara/Verbara.Platform/pull/32) (merge `fd2de9f0`,
ADR-0025) eliminates the 4 pod-restart cascade observed in B-LK 2026-05-24
under VU=1500 presence burst. Acceptance criterion: **pod restart count
delta = 0** across the 60-second peak-load window.

## Method

1. Baseline T0 — capture restart counts on freshly-rolled v2.5.2 pods.
2. Single-scenario sweep — `scripts/scenario-sweep.sh presence 1500`
   (presence VU=1500 × 60 s, against the medium-loadtest tenant).
3. Snapshot T1 — capture restart counts post-burst.
4. Delta = T1 - T0. Expected 0.

Reusable harness: [`scripts/rerun-blk-validation.sh`](../../../../scripts/rerun-blk-validation.sh).
Steps T0 → sweep → T1 → events scrape → archive copy.

## Results

### Pod restart count delta — ✅ 0 (acceptance criterion met)

| Component | T0 (01:01:44Z) | T1 (01:04:12Z) | Δ |
|---|---|---|---|
| platform-api × 2 | 0,0 | 0,0,0,0 (HPA scaled to 4) | **0** |
| platform-realtime × 4 | 0,0,0,0 | 0,0,0,0 | **0** |
| web × 1 | 0 | 0 | **0** |

vs prior v2.5.1 B-LK 2026-05-24: 4 platform-api restarts (3 on pod `7m4m4`,
1 on pod `w2nqh`).

### NBomber scenario result — qualitatively better

| Metric | v2.5.1 (2026-05-24 B-LK) | v2.5.2 (this run) | Δ |
|---|---|---|---|
| OK count | 34,755 | **43,184** | +8,429 (+24%) |
| Fail count | 1,000 (Unauthorized) | **0** | -1,000 (-100%) |
| p50 latency | 1,602 ms | **1,731 ms** | +8% (within noise) |
| p95 latency | 6,475 ms | **3,710 ms** | -43% ✅ |
| p99 latency | 12,419 ms | **3,711 ms** | -3.3× ✅ |
| HPA peak replicas | 5 | 4 | (one fewer needed) |
| Hottest node CPU | 51% | (within burst — not sampled) | n/a |

The 1,000 → 0 Unauthorized collapse is the headline result. The p99 reduction
from 12.4 s to 3.7 s is a strong second-order signal — the v2.5.1 tail was
dominated by requests timing out behind probe-killed-pod connection drops.

### Probe Unhealthy warnings — observed but did NOT trigger restarts

`kubectl get events -n r55-platform` recorded 3 Warning events during the
post-burst window:

```
5s   Warning   Unhealthy   pod/platform-api-57f8d6c7b6-vtgk9
                Liveness probe failed: Get "http://10.244.0.218:5000/health":
                context deadline exceeded (Client.Timeout exceeded while
                awaiting headers)

1s   Warning   Unhealthy   pod/platform-api-57f8d6c7b6-wclhq
                Readiness probe failed: ... /health/ready ... timeout

1s   Warning   Unhealthy   pod/platform-api-57f8d6c7b6-wclhq
                Liveness probe failed: ... /health ... timeout
```

**Note:** `/health` is now a no-op (`Predicate = _ => false`) so the request
handler itself is <1 ms. The probe timeout is the ASP.NET request pipeline
ahead of the handler (middleware chain `ErrorHandling → CORS → RateLimiter
→ TenantResolution → Authentication → Authorization`) competing for the
same Kestrel thread pool with 1500 VUs of load traffic.

The chart's defensive `livenessProbe.failureThreshold: 5` (vs prior 3) was
the safety net that caught these blips. Each pod recorded ≤ 2 consecutive
warnings, well under the 5-failure kill threshold.

**Implication for future hardening:** even with the no-op `/health` body,
the middleware chain can still queue probe responses past the timeout. The
ADR-0025 alternative #2 (dedicated Kestrel listener on a separate port for
probes, bypassing the middleware) becomes relevant if future scenarios push
past the failureThreshold=5 safety net. Filed as future work in ADR-0025
"Alternatives considered" — not justified by today's evidence.

## Cross-implications for the JTI investigation

[`docs/research/2026-05-24-jti-investigation-presence-vu1500.md`](../../../research/2026-05-24-jti-investigation-presence-vu1500.md)
identified `sync-over-async + SCAN+N×GET` in `JwtTokenService.GetCachedValidationKeys`
+ `RedisJwtKeyStore.GetAllAsync` as the likely root cause of 1,000
Unauthorized at v2.5.1 VU=1500. **That hypothesis remains technically valid
but did not need a direct fix** because the trigger (pod restart → empty
validation-key cache → first-burst fetch storm) was eliminated by PR #32.

Updated mental model:

```
v2.5.1 cascade:
  burst load → /health Postgres ping queued → 1s probe timeout × 3
    → pod restart → new pod empty cache → SCAN+N×GET under burst
    → cache fetch times out via sync-over-async → JwtBearer middleware
    → 401 Unauthorized × N (until cache fills on the new pod)

v2.5.2 outcome:
  burst load → /health no-op + 3s probe timeout × failureThreshold 5
    → no restart → cache stays warm → no 401 cascade
```

**Recommendation:** the Tier-1 JTI hardening (stale-cache fallback +
`ActiveKeyCacheTtl 60s → 300s` per `jti-investigation` Tier-1) is now LOWER
priority. The trigger is gone; the slow path remains latent. Ship the Tier-1
fix opportunistically (e.g., bundled with a future patch release) but it is
no longer blocking R5.5 progression.

## Bonus tooling fix — `jwt_exp_seconds` pipefail bug

During this validation cycle, `scripts/scenario-sweep.sh refresh_admin_token`
errored with `1779671789\n0: syntax error in expression`. Root cause: the
`jwt_exp_seconds` helper used a pipeline with `|| echo 0` fallback under
`set -o pipefail`. When `base64 -d` emitted a non-zero exit (padding edge
case) **after** `jq` had already printed the valid exp value to stdout, the
pipefail-driven exit triggered the fallback to also print "0" — producing
2-line output that broke the downstream `$((exp - now))` arithmetic.

Fixed in the same session (post-validation commit). Validated by running
the fixed sweep against v2.5.2 — produced the 43,184 OK / 0 fail result
above.

## Files

```
docs/operations/r55-blk-evidence/2026-05-25-v252-pr32-validation/
├── README.md                            ← this file
├── T0.txt                               ← pre-burst pod snapshot + restartCount
├── T1.txt                               ← post-burst pod snapshot + restartCount
├── events.txt                           ← kubectl events warning/killing tail
├── sweep-stdout.log                     ← full NBomber sweep output
├── hardware/                            ← (empty — no live-burst sampling this rerun)
└── presence-VU1500-archive/             ← NBomber report triple (csv/html/md) + log
```

## Sign-off

PR #32 + chart defensive bump validated end-to-end against the originally-
documented failure mode (R5.5 Phase B-LK 2026-05-24 evidence pack). Plan
B-LK presence VU=1500 envelope on v2.5.2 is now:

| Property | Value |
|---|---|
| Aggregate throughput | ~720 req/s sustained |
| Total requests served | 43,184 in 60 s |
| Failure rate | 0% |
| p50 / p95 / p99 latency | 1,731 / 3,710 / 3,711 ms |
| Pod restarts | 0 |
| Probe-Unhealthy warnings | 3 (failureThreshold=5 absorbed) |
| HPA replicas peak | 4 (of max=8) |

**Closure:** ADR-0025 validation loop closed. R5.5 Phase B-LK envelope for
v2.5.2 documented + cross-referenced from the original 2026-05-24 evidence
pack.
