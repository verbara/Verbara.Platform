# R5.5 Phase D-L · 24h soak — closure artifacts

Run window: **2026-04-29 05:07:39 -05:00 → 2026-04-30 05:00 -05:00** (24h continuous, presence_broadcast read-only scenario, VU=500 sostenido).

## Files

| File | Source | Purpose |
|---|---|---|
| `soak-drift-2026-04-29.csv` | `scripts/soak-drift-snapshot.sh` | Hourly metrics snapshot (api_rss_mb, api_cpu_pct, pg_rss_mb, pg_conns, p99_ms, rps, kestrel_conns, disk_free_gb, prom_tsdb_mb) — 25 rows (T0 baseline + 24 hourly + final idle reading). |
| `soak-24h-presence-2026-04-29.log` | `scripts/scenario-sweep.sh presence` | NBomber driver loop: 144 steps × 600s @ VU=500. Per-step: ok count, fail count, latency p50/p75/p95/p99, status code distribution. |
| `soak-log-watchdog-2026-04-29.log` | `scripts/soak-log-watchdog.sh` | Truncate-loop journal: every 5 min, list of containers truncated + freed bytes + post-truncate `df /` snapshot. ~262 truncations across 24h. |
| `soak-drift-snapshot-2026-04-29.log` | `scripts/soak-drift-snapshot.sh` | Drift collector journal: human-readable echo of each CSV row appended (hourly cadence). |

## Headline numbers

- **958 525 165 OK** / **0 fails** across 144 steps (~959 M requests in 24h).
- p99 latency: **min 55.10 ms · max 88.83 ms · avg 60.66 ms** (budget ≤100 ms — never exceeded).
- API RSS: 351.9 MB → 432 MB. **+80 MB drift the first 4h, plateau the remaining 20h.** No memory leak signature.
- Postgres connections: **stayed 12-13 throughout** (Phase 2 single-pool architecture sustained 24h).
- RPS: ~11 000 sustained · Kestrel conns: 501 sustained.
- Disk free: 216 GB → 218 GB (watchdog truncó ~5 GB cada 5 min ≈ **1.5 TB total log churn truncated**).

## Reproducer

```bash
# Driver
LADDER=$(printf '500 %.0s' {1..144})
SCENARIO_SWEEP_DURATION_SEC=600 SCENARIO_COOLDOWN_SEC=2 \
PLATFORM_API_URL=http://localhost:5000 \
./scripts/scenario-sweep.sh presence $LADDER \
  > /tmp/soak-24h-presence-$(date +%Y-%m-%d).log 2>&1 &

# Operational guards
nohup ./scripts/soak-log-watchdog.sh --threshold-gb 5 --interval-sec 300 \
  > /tmp/soak-log-watchdog.log 2>&1 & disown
nohup ./scripts/soak-drift-snapshot.sh \
  > /tmp/soak-drift-snapshot.log 2>&1 & disown
```

## Pre-conditions verified

- Platform v1.14.6 (ADR-0015 Phase 2 — shared NpgsqlDataSource) deployed.
- `docker-compose.smb.yml` (`max_connections=200`, `shared_buffers=512MB`).
- 12 containers up (6 app + 6 r55-obs).
- Layer 1 (NodeDiskSpaceLow alert) + Layer 2 (watchdog) active. Layer 3 (Docker daemon log rotation) still PENDING — recommended before next soak.

See full closure write-up in `docs/operations/soak-test-report-local.md`.
