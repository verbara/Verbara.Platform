# Soak Test Report · Docker Compose local (R5.5 Phase D-L)

**Status:** ✅ **PASS** — 24h completed without P0 leak.

**Run window:** 2026-04-29 05:07:39 -05:00 → 2026-04-30 05:00 -05:00 (24 h calendar-time).

**Stack under test:**

- Asterisk.Platform v1.14.6 (ADR-0015 Phase 2 — shared `NpgsqlDataSource`).
- Asterisk.Sdk.Pro 1.16.0-pro (ADR-0008 shared `NpgsqlDataSource` overload across 9 storage packages).
- `docker/docker-compose.smb.yml` SMB tier (Postgres 18 alpine, `max_connections=200`, `shared_buffers=512MB`, `effective_cache_size=2GB`; Redis 8 alpine; 4 ASP.NET Core replicas not used — single platform-api container).
- 12 containers up (6 app + 6 r55-obs: Prometheus, Grafana, Loki, Alertmanager, node-exporter, blackbox-exporter).

**Hardware:** AMD Ryzen 9 9900X · 60 GB RAM · NVMe `/dev/nvme0n1p2` 274 GB ext4 mounted on `/`.

---

## Method

Driver: `scripts/scenario-sweep.sh presence` looping **144 steps × 600 s @ VU = 500** (= 24 h calendar-time; admin JWT refreshed every step against the 15-min token TTL).

Scenario: `presence_broadcast` against `GET /api/v1/admin/agents` (read-only NBomber scenario from
`tests/Asterisk.Platform.LoadTests/Scenarios/PresenceBroadcastScenarios.cs`). HTTP-only — SIPp companion (`03-queue-join`) deferred to a Phase D-L.5 follow-up; first iteration validates the harness + isolates drift to the API layer.

Operational guards (running for the full 24 h):

- `scripts/soak-log-watchdog.sh --threshold-gb 5 --interval-sec 300` — truncates any container's `*-json.log` over 5 GB every 5 min (Layer 2 of the disk-fill unblock; see `docs/operations/alerts-runbook.md` § NodeDiskSpaceLow).
- `scripts/soak-drift-snapshot.sh` — appends a CSV row every hour to `tests/Asterisk.Platform.LoadTests/soak-reports/soak-drift-2026-04-29.csv` (api_rss_mb, api_cpu_pct, pg_rss_mb, pg_conns, p99_ms, rps, kestrel_conns, disk_free_gb, prom_tsdb_mb).

Synthetic monitoring + Grafana dashboards from Phase 0L active throughout (Phase E-L verification deliverable). NodeDiskSpaceLow P0 alert (commit `8042d7d`) armed as safety net — never fired.

---

## Results headline

| Metric | Value | Budget | Verdict |
|---|---:|---:|---|
| Steps completed | 144 / 144 | 144 | ✅ |
| Total OK responses | **958 525 165** (~959 M) | — | ✅ |
| Total fails | **0** | <1 % | ✅ (zero) |
| p99 latency — average per step | **60.66 ms** | ≤100 ms | ✅ |
| p99 latency — minimum | 55.10 ms | — | — |
| p99 latency — maximum | 88.83 ms | ≤100 ms | ✅ |
| API RSS drift | 351.9 MB → 432 MB | <100 MB / 24h | ⚠ +80 MB warm-up, then plateau (acceptable — see § Drift analysis) |
| Postgres connection count | 12 → 13 (peak 13) | ≤14 single-pool | ✅ Phase 2 invariant sustained 24h |
| RPS sustained | ~11 000 | n/a (SLO is latency, not RPS) | ✅ |
| Kestrel connections | 501 sustained | matches VU=500 + 1 driver = 501 | ✅ |
| Disk free `/` | 216 GB → 218 GB | ≥10 % free | ✅ (watchdog truncated ~1.5 TB log churn) |
| NodeDiskSpaceLow alert fires | 0 | 0 | ✅ |

## Drift analysis

### API RSS

```
T0 (05:07)  351.9 MB
T+1h        364.3 MB
T+2h        418.8 MB
T+3h        423.2 MB
T+4h        428.6 MB  ← warm-up plateau reached
T+5..15h    range 433-460 MB
T+16..24h   range 425-435 MB  ← system "settled" after first 4h
final       432.0 MB
```

**Interpretation:** GC reached its working-set steady state by hour 4. The remaining 20h showed ±15 MB drift around 435 MB with no upward trend — no memory leak signature. Native AOT + bounded LRU caches (`ResolvedTenantCache`, `RoleClaimsCache`, etc., shipped en AHH train v1.14.0) keep the working set tight.

### Postgres connections

12 to 13 connections throughout the 24h — confirms the **Phase 2 single-pool architecture** (one `NpgsqlDataSource` shared across the 14 storage packages) holds under sustained load. Pre-Phase-2 this same workload would have demanded up to **14 × 100 = 1 400 connections** (sprawl bug fixed in v1.14.5 Phase 1 + v1.14.6 Phase 2).

### Latency p99

p99 distribution across 143 measurable steps (one final step idle, no p99):

```
min  55.10 ms
max  88.83 ms
avg  60.66 ms
```

**Interpretation:** consistently under the 100 ms budget. p99 was even lower (~57 ms) during the second half of the soak — the system warmed up and stayed there. No degradation pattern.

### Disk

Without the watchdog, platform-api alone generated **~150 GB / hour** of JSON container logs at this rate (measured during a previous failed attempt, 27 GB in 11 min). The watchdog truncated **~5.3 GB every 5 min** (~63 GB/h sustained), keeping `/` flat at 216-218 GB free for the entire run. That is **~1.5 TB of log churn handled by truncation in 24 h.**

This is operationally acceptable as defense-in-depth, but Layer 3 (architectural fix — `daemon.json` `log-opts: max-size: 100m, max-file: 5`) remains **PENDING** and is a pre-condition for any future soak (D-LK K8s, D-C cloud).

---

## What this validates

1. **Asterisk.Platform v1.14.6 read-path can sustain ~11 k req/s × 24 h with zero failures and p99 stable under 100 ms** — independent confirmation of the Phase 2 SMB tier knee envelope measured in Phase C-L.
2. **No memory leak** detectable in Platform.Api or Postgres after 24 h of continuous traffic.
3. **ADR-0015 Phase 2 single-pool architecture holds in real time** — connection count never exceeded 13. The Postgres-side `max_connections=200` setting in `docker-compose.smb.yml` provides ~15× operator headroom.
4. **Phase 0L observability stack** (Prometheus, Loki, Alertmanager, blackbox-exporter, NodeDiskSpaceLow alert) is production-grade for 24h+ continuous operation.
5. **Operational unblock for the disk-fill issue** (Layers 1+2) is sufficient to ship a 24 h soak; Layer 3 should still be applied before relying on this in production.

## What this does NOT validate (deferred to follow-ups)

- **SIPp pairing** (`03-queue-join` populating `live-queue` analytics snapshot store) — requires Phase D-L.5 follow-up.
- **Write-path soak** — only `presence_broadcast` (read-only) was exercised. POST/PUT/DELETE soak deferred (would require quota/billing event ingestion + Argon2id-bound login rate ≤75 req/s sweep paired with the read-path scenario).
- **K8s soak (D-LK)** — separate deliverable. Phase 0LK setup is a pre-condition.
- **Cloud soak (D-C)** — separate deliverable, Phase 0C dependency.
- **Phase 2 invariant under multi-replica** — `scale.yml` 4-replica math (4 × 1 × 50 = 200 conns vs `max_connections=220`) was not exercised in this single-replica soak. Multi-replica soak deferred.

---

## Closure actions

- ✅ Background guards killed (PIDs 80573 + 448988).
- ✅ Drift CSV preserved at `tests/Asterisk.Platform.LoadTests/soak-reports/soak-drift-2026-04-29.csv`.
- ✅ Driver + watchdog + drift logs archived to `tests/Asterisk.Platform.LoadTests/soak-reports/` with explanatory `README.md`.
- ✅ Plan `docs/plans/active/2026-04-27-r5.5-execution-plan.md` Phase D-L tasks marked done.
- ✅ Memory `project_dl_soak_24h_pass.md` created with run details.
- ✅ Final NBomber session report committed at `tests/Asterisk.Platform.LoadTests/load-test-reports/nbomber_report_2026-04-30--10-02-52.{md,csv,html}` (~350 KB total — within the repo's existing convention; per-step `nbomber-log-*.txt` files remain gitignored).
- ⏳ Layer 3 architectural fix (`/etc/docker/daemon.json` log rotation) — track as next operational hardening before D-LK / D-C soaks.

---

## References

- Plan task spec: `docs/plans/active/2026-04-27-r5.5-execution-plan.md` § Phase D-L (lines 4215-4295).
- Disk-fill unblock saga: `docs/operations/alerts-runbook.md` § NodeDiskSpaceLow (commits `8042d7d` + `6146534`).
- Phase 2 architecture: `docs/decisions/0015-postgres-pool-sprawl-mitigation.md` Phase 2 + Pro `docs/decisions/0008-shared-npgsqldatasource-overload.md`.
- Phase C-L baseline (Phase 2 SMB tier): `docs/operations/load-test-baseline.md` § "Phase C-L SMB tier post-Phase-2".
- Driver script: `scripts/scenario-sweep.sh` + scenario `tests/Asterisk.Platform.LoadTests/Scenarios/PresenceBroadcastScenarios.cs`.
- Operational guards: `scripts/soak-log-watchdog.sh` + `scripts/soak-drift-snapshot.sh`.
