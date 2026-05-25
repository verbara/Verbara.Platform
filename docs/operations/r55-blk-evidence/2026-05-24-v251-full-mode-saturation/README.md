# v2.5.1 K8s saturation snapshot — full default suite (NOT a B-LK baseline)

**Date:** 2026-05-24 21:49 UTC
**Target:** `http://api.r55.local` (Cilium Gateway → `r55-platform/platform-api:5000`, 2 replicas, HPA min=2 max=8)
**Image:** `ghcr.io/verbara/platform/api:v2.5.1` (digest `sha256:b61b062f…`)
**Tooling:** `dotnet run --project tests/Verbara.Platform.LoadTests -c Release` (full default mode, all 5 scenarios in parallel)

## TL;DR — methodology mistake captured as data

This is **not** a B-LK baseline run. It is the saturation snapshot produced by
running NBomber's full default suite (all 5 scenarios in parallel at the
hard-coded design rates — JWT 2000 RPS + Presence VU=1500 + LiveQueue 500 RPS +
AgentAssist 50 RPS + Queues 17 RPS). The aggregate load (~2.5 k RPS, all
authenticated) exceeds the K8s lab envelope and triggered NBomber's auto-stop
on JWT failure-count threshold at T+4 s.

Phase B-LK calls for the per-scenario `scripts/scenario-sweep.sh` ladder
methodology (5 increasing rates × 60 s each, per scenario), not the
parallel-stress default suite. See `docs/plans/active/2026-04-27-r5.5-execution-plan.md`
§ Task B-LK.1 and `scripts/scenario-sweep.sh` for the correct invocation.

## What the data shows

| Scenario | Rate / VU | Duration | OK | Fail | Status codes |
|---|---|---|---|---|---|
| `jwt_issuance_validation` | inject 2000 req/s | 4 s (auto-stop) | 0 | 7741 | 5389 503 · 2352 401 |
| `queue_ingestion` | inject 17 req/s | 4 s | 27 | 56 | 27 200 · 56 503 |
| `presence_broadcast` | KeepConstant 1500 VU | 4 s | 2160 | 7071 | 2160 200 · 7071 503 |
| `live_queue_snapshot_write` | inject 500 req/s | 4 s | 0 | 2425 | 879 404 · 1546 503 |
| `agent_assist_session_start` | inject 50 req/s | 4 s | 96 | 148 | 96 200 · 148 503 |

Aggregate: ~3.1 k requests/s sustained for 4 s, ~62 % 503 ServiceUnavailable
(rate-limiter pressure), ~12 % 401 (auth queue saturation — Argon2id +
JwtTokenService can't issue 2 k tokens/s on 2 replicas).

## How to read this for B-LK design

- **HPA never had time to scale** (4 s window vs ~30 s HPA evaluation period)
  → for v2.5.1 baseline runs that exercise auth, start with rate-ladder
  steps low enough that HPA has time to react at each step (60 s/step is
  default and adequate).
- **Auth throughput ceiling on 2 replicas** is somewhere below 2 k RPS sustained.
  Plan B-LK should run the JWT sweep ladder (`./scripts/scenario-sweep.sh jwt
  10 50 100 250 500`) against v2.5.1 to find the actual knee.
- **Rate-limiter behaviour** dominated the failures; absolute Postgres / Redis
  utilization wasn't the bottleneck (the run was too short to measure
  resource exhaustion). Phase C-LK stress sweep is the right place to push
  past the limiter and measure resource exhaustion.

## Action items

1. ✅ Files preserved here so the saturation snapshot is not lost when NBomber's
   next run wipes `tests/Verbara.Platform.LoadTests/load-test-reports/`.
2. ⏳ Run `scripts/scenario-sweep.sh jwt` against `http://api.r55.local` for the
   real B-LK.1 JWT measurement.
3. ⏳ Same for `queues`, `livequeue`, `agentassist`, `presence`.
4. ⏳ Cross-correlate with B-L docker baseline (v1.14.6 historical numbers in
   `docs/operations/load-test-baseline.md`) and document v2.5.1 K8s delta in
   B-LK.5.
