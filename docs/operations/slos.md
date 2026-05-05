# Service-Level Objectives — Verbara.Platform

**R5.4 Track A · S5.2** · Authored 2026-04-26 · **R5.5 Phase F refresh 2026-04-27** · Owner: Platform SRE

> **STATUS — v1 partial-measured (2026-04-27).** R5.5 Phase B-L produced the
> first authoritative measurements for the **Auth + JWT** path (§1 below).
> Sections 2 – 5 remain v1 **provisional** because the R5.4 NBomber scenarios
> targeted endpoints that don't exist on the current Platform.Api surface
> (queue ingestion is SIP-driven, presence is SignalR-driven, agent-assist is
> system-initiated) — see [`load-test-baseline.md`](load-test-baseline.md)
> "Findings" + the R5.5 P1 follow-up tracker for the 3 still-pending scenario
> rewrites. Sections 6 – 8 (storage / Redis / resilience layer) inherit from
> the R5.4 estimates; the Phase C-L chaos run validated the recovery /
> resilience targets (see [`chaos-test-report-local.md`](chaos-test-report-local.md))
> but does not produce continuous-throughput numbers.
>
> **Measured datapoints in this document are flagged inline with `🟢 measured`.**

---

## Why these SLOs exist

Per [ADR-0009](../decisions/0009-slo-baseline-alert-severity-model.md), Verbara.Platform
publishes canonical SLOs so:

- Operators can distinguish "degraded but in-budget" from "violation" without
  paging-judgement calls.
- Buyer / enterprise procurement teams can answer "what does the platform commit
  to?" with a single document instead of vendor-specific marketing.
- The 3-tier alert severity model (P0/P1/P2 — see `alerts.yml` + `alerts-runbook.md`)
  derives its thresholds from these targets, not the other way around.

Targets follow `observed_p99 × 1.2` headroom rule (ADR-0009). The 1.2 multiplier
absorbs short-term burst noise without paging on transient blips.

---

## Service tiers

| Tier | Availability target | Audience | Notes |
|---|---|---|---|
| **Free baseline** | 99.5% per calendar month | Open-source / self-host / community | Single-node; Postgres mandatory |
| **Enterprise** | 99.9% per calendar month | Commercial deployments on Pro 1.13.0+ | Cluster ≥ 3 nodes; Redis cache enabled; Pro retention enabled |

Availability windows reset at the first of each calendar month UTC. Budget burn
rate alerts (P1) fire at 50% burn over 6h on Enterprise tier.

---

## SLO catalogue (v1 provisional)

### 1. Auth + JWT 🟢 measured

> **R5.5 Phase B-L refresh (2026-04-27).** JWT issuance + validation baseline
> was measured on the docker-compose.full.yml staging stack via
> `scripts/jwt-sweep.sh` (5 sequential rates × 60 s each on AMD Ryzen 9 9900X /
> 60 GB / single-instance Platform.Api + Postgres 17). Real ceiling on this
> hardware is **50 – 75 req/s sustained** for stable p99; beyond 100 req/s the
> latency tail explodes. Targets below split "measured" (R5.5) from "v1
> provisional" (still inferred from architecture). See
> [`load-test-baseline.md`](load-test-baseline.md) for the full sweep.

| Metric | Target | Status | Source meter / instrument |
|---|---|---|---|
| Login latency p99 (single-instance, ≤ 50 req/s) | ≤ 250 ms | 🟢 **measured 213 ms** | Platform API HTTP histogram on `/auth/login` (`http_server_request_duration_seconds_bucket`) |
| Login latency p99 (single-instance, ≤ 100 req/s) | ≤ 700 ms | 🟢 **measured 671 ms** — over-budget vs original ≤ 200 ms target; tail explodes due to per-request DataProtection EF round-trip | same |
| Login throughput knee (single-instance) | < 250 req/s | 🟢 **measured: collapse at 250 req/s** (44 % success, p99 = 56.9 s); 500 req/s ≈ pure saturation | same |
| Login success rate (within capacity) | ≥ 99.5 % (excluding bad credentials) | 🟢 measured **100 %** at ≤ 100 req/s | same |
| `/health` p99 idle | ≤ 50 ms | 🟢 measured **22.8 ms** (200-ping smoke after the HTTP-meter expose fix) | `http_server_request_duration_seconds_bucket{http_route="/health"}` |
| JWT validation latency p99 (cached key, no DB hit) | ≤ 50 ms | v1 provisional — no isolated test yet | `Verbara.Platform.Auth.JwtKeyRotation` · `jwt.key.rotation.duration` |
| Token refresh latency p95 | ≤ 100 ms | v1 provisional | Platform API HTTP histogram on `/auth/refresh` |
| Active validation keys | ≥ 1, ≤ 4 | v1 provisional | `Verbara.Platform.Auth.JwtKeyRotation` · `jwt.keys.active` |
| MFA challenge generation latency p95 | ≤ 200 ms | v1 provisional | Platform API HTTP histogram on `/auth/mfa/*` |

**Bottleneck identified at the knee:** per-request DataProtection
`EntityFrameworkCoreXmlRepository` round-trip (one Postgres SELECT
against `data_protection_keys` per JWT issuance). Confirmed by the
linear latency rise as rate climbs (175 ms at r=10 → 396 ms mean at
r=100), then the catastrophic flip at r=250 when the connection pool
fills. **Path to lift the ceiling:** in-memory key-ring cache promotion +
multi-replica Platform.Api + Postgres pool tuning (each delta tracked as
v1.13.x patch — JWT-001 in `docs/roadmap.md`).

### 2. Queue ingestion (call session events)

> **TBD: re-baseline post-S5.1** — `queue_ingestion` scenario targets ~17 req/s
> with bursts. Values assume single Postgres + Pro.EventStore wired.

| Metric | Target (v1 provisional) | Source meter / instrument |
|---|---|---|
| Event append latency p99 | ≤ 200 ms | `Verbara.Sdk.Pro.EventStore` · `eventstore.persist.duration_ms` |
| Events appended success rate | ≥ 99.9% | `eventstore.events.appended` / (`eventstore.events.appended` + `eventstore.events.skipped`) |
| Projection lag p95 | ≤ 5 s | `eventstore.projection.lag_ms` |
| Subscriber inflight (saturation) | < 100 | `eventstore.subscriber.inflight` (gauge) |

### 3. Realtime presence (PlatformHub)

> **TBD: re-baseline post-S5.1** — `presence_broadcast` scenario sustains 1,500
> virtual users. Values assume Pro.Push.SignalR + Pro.Push backplane (Redis or Postgres).

| Metric | Target (v1 provisional) | Source meter / instrument |
|---|---|---|
| Hub connection open success rate | ≥ 99.5% | `Verbara.Sdk.Pro.Push.SignalR` · `hub.connections.opened` vs `hub.connections.closed` (clean) |
| Presence merge end-to-end latency p95 | ≤ 1 s | `presence.merges.applied` rate vs `presence.heartbeats.published` lag |
| Presence broadcast fanout p99 | ≤ 500 ms | `presence.broadcasts.fanout` (per-message duration if instrumented; otherwise observed via histogram) |
| Push relay backlog | < 1000 events | `Verbara.Sdk.Pro.Push.Postgres` · `push.postgres.backlog` |
| Push backplane availability | ≥ 99.9% | `push.postgres.listen_healthy` (gauge: 1=healthy) |

### 4. Live queue snapshot writer (Pro.Analytics.Live, R5.1 Task G)

> **TBD: re-baseline post-S5.1** — `live_queue_snapshot_write` scenario sustains
> 500 reads/s; writer coalesces at ~5 Hz per `(tenant, queue)`.

| Metric | Target (v1 provisional) | Source meter / instrument |
|---|---|---|
| Snapshot write latency p99 | ≤ 50 ms | `Verbara.Sdk.Pro.Analytics.Live` · `live_queue.snapshots.write_duration_ms` |
| Write error rate | < 1% | `live_queue.snapshots.write_error` / `live_queue.snapshots.published` |
| Writer inflight (saturation) | < 50 | `live_queue.writer.inflight` (gauge) |
| Throttle ratio (expected) | 80–95% suppressed | `live_queue.snapshots.throttled` / total emissions |
| Read API p99 (Platform endpoint) | ≤ 100 ms | Platform API HTTP histogram on `/queues/{name}/live` |

### 5. AgentAssist (Pro.AgentAssist)

> **TBD: re-baseline post-S5.1** — `agent_assist_session_start` scenario at 50
> starts/s. Latency dominated by external STT/LLM provider RTT.

| Metric | Target (v1 provisional) | Source meter / instrument |
|---|---|---|
| Session start success rate | ≥ 99% | `agentassist.sessions.started` vs `agentassist.snoop.errors` |
| Suggestion latency p95 | ≤ 2 s | `agentassist.suggestion.latency_ms` |
| Transcription latency p95 | ≤ 1.5 s | `agentassist.transcription.latency_ms` |
| Sessions active (saturation) | < 500 per node | `agentassist.session.active` |
| LLM inflight per provider | < 20 | `agentassist.llm.inflight` |
| Transcription queue depth | < 100 | `agentassist.transcription.queue_depth` |

### 6. Storage — Postgres

> **TBD: re-baseline post-S5.1** — derived from Npgsql 10 connection pool sizing
> defaults + observed query patterns in IT suite.

| Metric | Target (v1 provisional) | Source meter / instrument |
|---|---|---|
| Connection pool utilization | < 80% | Npgsql `db.client.connections.usage` |
| Postgres health check | `Healthy` | `BackgroundServiceHealthCheck` + `PostgresHealthCheck` |
| Slow query rate (>500 ms) | < 1% of total | Npgsql `db.client.commands.duration` p99 |
| Retention purge duration | < 30 min per nightly run | `Verbara.Sdk.Pro.Storage.Common.Retention` · `retention.duration_ms` |
| Retention rows purged (sanity) | matches dry-run prediction ±10% | `retention.purged` vs `retention.dry_run.would_purge` |

### 7. Storage — Redis (Identity + cache)

> **TBD: re-baseline post-S5.1** — Identity.Redis package shipped in R5.1; JTI
> revocation cache + presence backplane share the same instance.

| Metric | Target (v1 provisional) | Source meter / instrument |
|---|---|---|
| Redis ping latency p99 | ≤ 10 ms | StackExchange.Redis built-in counters (TBD: meter to add in v1.13.x) |
| Redis memory utilization | < 75% | Redis `INFO memory` (scraped via redis_exporter) |
| JTI cache hit rate | ≥ 95% | TBD: meter to add in v1.13.x — currently tracked via `IJtiRevocationCache` logs |
| Backplane `listen_healthy` | 1 (gauge) | `push.postgres.listen_healthy` (Postgres) — equivalent Redis gauge TBD |

### 8. Resilience layer (cross-cutting) 🟢 measured (chaos)

| Metric | Target | Status | Source meter / instrument |
|---|---|---|---|
| Circuit breakers in `Open` state | 0 sustained > 5 min | 🟢 R5.5 C-L: held during all 10 chaos events; closed cleanly post-recovery | `Verbara.Sdk.Resilience` · `circuit.state` (value 2 = Open) |
| Retry attempts rate per policy | < 0.5/s sustained | v1 provisional | `retry.attempts` |
| Per-attempt timeouts | < 1% of attempts | v1 provisional | `timeout.fired` |
| Postgres SIGKILL recovery time | ≤ 60 s | 🟢 R5.5 C-L: ~30 s (compose `up -d --wait` gate) | observed via `up{job="platform-api"}` flap |
| Asterisk SIGKILL recovery time | ≤ 60 s | 🟢 R5.5 C-L: ~30 s | observed via `up{job="asterisk-ami-tcp"}` |
| Platform.Api SIGKILL recovery time | ≤ 90 s | 🟢 R5.5 C-L: ~60 s (HC stale window included) | observed via `probe_success{instance="…/health"}` |
| Net-layer chaos recovery (loss/delay/rate) | immediate after lift | 🟢 R5.5 C-L: sub-scrape-window | qdisc-injected via `pumba netem --tc-image gaiadocker/iproute2` |

---

## v2 enterprise (aspirational)

These targets require hardware + scale assumptions that the v1 baseline does not
mandate. They are **non-binding** in v1 — published as a north-star for the v2
Enterprise tier (planned post-R5.4 once a measured baseline exists across multi-node
clusters):

| Service class | v2 enterprise aspirational |
|---|---|
| Auth + JWT | p99 ≤ 25 ms · 99.95% availability |
| Queue ingestion | p99 ≤ 100 ms · 99.99% durability via WAL replication |
| Realtime presence | p95 fanout ≤ 200 ms · 100k concurrent connections |
| AgentAssist | p95 first suggestion ≤ 1.2 s · multi-region active-active |
| Storage Postgres | < 50% pool utilization at peak · automated read-replica failover |

Hardware + scale assumptions for v2: ≥ 3 cluster nodes (8 vCPU / 32 GB RAM each),
Postgres 17 with streaming replication + 2 read replicas, Redis Sentinel quorum
of 3, dedicated NVMe storage class. Documented in [`capacity.md`](capacity.md)
once S5.7 ships.

---

## Review cadence

| Cadence | Activity |
|---|---|
| Weekly (P2 review) | Operator reviews P2 alerts firing in the prior 7d; adjust thresholds via PR if a noise pattern emerges. |
| Monthly | SRE on-call publishes burn-rate report (per-tier availability vs target). |
| Quarterly | Re-run S5.1 NBomber baseline on staging; refresh this doc per the procedure below. |
| Major release (every R) | Re-baseline if any P1+ instrument gained or changed dimensions. |

---

## How to refresh from real data

When the first S5.1 baseline run completes on staging:

1. Open [`docs/operations/load-test-baseline.md`](load-test-baseline.md) and read
   the Observed p50 / p95 / p99 columns for each scenario.
2. For each row in this doc:
   - Set `Target (v1)` = observed `p99 × 1.2` (rounded sensibly — e.g. round to
     nearest 10 ms below 100 ms, nearest 50 ms above).
   - Update the source meter column if the meter name has changed since
     v1.12.0-pro.
3. Remove the "v1 provisional" banner at the top of this document.
4. Append a footnote: `Refreshed from S5.1 baseline YYYY-MM-DD by [operator]`.
5. Commit: `docs(operations): refresh SLOs from S5.1 baseline`.

Subsequent quarterly reviews follow the same procedure with the most recent
baseline run. Every refresh that lowers an SLO target (i.e. tightens the
contract) also requires updating `alerts.yml` thresholds proportionally and
rerunning `promtool check rules`.

---

## References

- [ADR-0009](../decisions/0009-slo-baseline-alert-severity-model.md) — SLO baseline + alert severity model
- [`load-test-baseline.md`](load-test-baseline.md) — S5.1 results template
- [`alerts.yml`](alerts.yml) — Prometheus alert rules derived from these SLOs
- [`alerts-runbook.md`](alerts-runbook.md) — Per-alert what / why / first response
- [`resilience-runbook.md`](resilience-runbook.md) — Resilience meter golden signals
- Verbara.Sdk.Pro `docs/architecture.md` § "Meter catalog" — full instrument inventory
