# Capacity Planning Baseline — Asterisk.Platform

**R5.4 Track C · S5.7** · Authored 2026-04-26 · Owner: Platform SRE

> **STATUS — v1 provisional (2026-04-26).** Numbers below are conservative
> engineering estimates derived from architecture review + industry benchmarks +
> the Pro 1.15.0-pro meter catalog. The S5.1 NBomber suite is shipped; the first
> authoritative run on staging is pending. Refresh per the procedure below once
> measured baseline data exists.

---

## Why this document exists

Per [ADR-0009](../decisions/0009-slo-baseline-alert-severity-model.md) and the
R5.4 Production Validation track, capacity planning publishes the **expected
operating envelope per sizing tier** so operators and procurement teams can
answer:

- "How big a node / cluster do I need to support N tenants × M concurrent calls?"
- "When do I add a node vs scale up an existing one?"
- "Which subsystems will saturate first, and what's the meter to watch?"

All numeric guidance below should be read as **engineering estimate, not measured
truth**, until the S5.1 baseline runs on staging and refreshes this document
through the procedure at the bottom.

---

## Sizing tiers

The four tiers below frame typical CCaaS deployments from a small support team
through multi-tenant enterprise. Each tier is bounded by the SLOs in
[`slos.md`](slos.md) — beyond a tier's envelope, expect SLO breaches (in
particular `SloBreachQueueIngestion` and `PresenceBacklogGrowing` from
[`alerts.yml`](alerts.yml)).

| Tier | Tenants | Concurrent calls | Agents | Queues | Cluster nodes | Typical use case |
|---|---|---|---|---|---|---|
| **Small** | 1–5 | ≤ 50 | ≤ 25 | ≤ 10 | 1 (single node) | Single business / pilot / dev sandbox |
| **Medium** | 5–25 | 50–250 | 25–100 | 10–40 | 2 (active/passive) | Mid-market BPO / multi-team support |
| **Large** | 25–100 | 250–1,000 | 100–500 | 40–150 | 3 (active/active) | Enterprise contact center / multi-brand |
| **XL** | 100–500 | 1,000–5,000 | 500–2,500 | 150–500 | 5+ (sharded) | Multi-region BPO / SaaS reseller |

**Estimate basis:** ~10 concurrent calls per agent (industry CCaaS standard),
~25 agents per queue median, .NET 10 AOT throughput benchmarks (roughly 4× CLR
JIT for cold-path JWT validation), 4-core typical Asterisk node sizing in
production.

---

## Per-instance resource sizing

Each subsystem table prescribes **per-instance** sizing — multiply by node count
in the cluster column above. All values are conservative estimates pending S5.1
measurement.

### Platform.Api (per node)

| Tier | CPU | RAM | Disk | Notes |
|---|---|---|---|---|
| Small | 2 vCPU | 4 GB | 20 GB SSD | Combined with Postgres on same host acceptable |
| Medium | 4 vCPU | 8 GB | 40 GB SSD | Dedicated host; Pro retention enabled |
| Large | 8 vCPU | 16 GB | 80 GB SSD | Pro 1.13.0+ + Identity.Redis JTI cache enabled |
| XL | 16 vCPU | 32 GB | 160 GB SSD | + L7 reverse proxy + sticky sessions for SignalR |

**Rationale:** .NET 10 Native AOT cold-start ≤ 200 ms + steady-state RSS
~250 MB baseline + ~5 MB per active SignalR connection. Disk sized for log
rotation (7d retention by default) + ~100 MB DataProtection key store.

### Postgres (primary)

| Tier | CPU | RAM | Disk IOPS | Disk size | Tuning |
|---|---|---|---|---|---|
| Small | 2 vCPU | 4 GB | 1k IOPS | 50 GB | `shared_buffers=1GB` · `max_connections=100` |
| Medium | 4 vCPU | 16 GB | 3k IOPS | 200 GB | `shared_buffers=4GB` · `max_connections=200` · WAL on separate volume |
| Large | 8 vCPU | 32 GB | 8k IOPS NVMe | 500 GB | `shared_buffers=8GB` · `max_connections=400` · 1× streaming replica |
| XL | 16 vCPU | 64 GB | 20k IOPS NVMe | 2 TB | `shared_buffers=16GB` · `max_connections=800` · 2× read replicas + connection pooler (PgBouncer) |

**Rationale:** Sized against the partitioned `session_events` table + JSONB
write rate from `Asterisk.Sdk.Pro.EventStore` (~200 ms p99 SLO budget per
[`slos.md`](slos.md) §2). Retention purges (`Asterisk.Sdk.Pro.Storage.Common.Retention`)
keep storage growth bounded but require IOPS headroom during the nightly 2am UTC
cron window.

### Redis (cluster cache, optional Small / mandatory Large+)

| Tier | CPU | RAM | Notes |
|---|---|---|---|
| Small | — | — | Optional; in-memory `IJtiRevocationCache` adequate |
| Medium | 1 vCPU | 1 GB | Single node; `maxmemory-policy=allkeys-lru` |
| Large | 2 vCPU | 4 GB | Sentinel quorum of 3 recommended |
| XL | 4 vCPU | 16 GB | Redis Cluster mode (≥ 3 shards × 3 replicas) |

**Rationale:** JTI revocation cache (R5.1 `Asterisk.Platform.Identity.Redis`) +
Pro.Push.Redis backplane share the same instance. Memory dominated by:
~150 bytes per revoked-JTI entry × MFA session count, plus presence merges in
flight. `RedisMemoryHigh` P2 alert from [`alerts.yml`](alerts.yml) fires at 75%
utilization sustained 1h.

### Asterisk (per node)

| Tier | CPU | RAM | Notes |
|---|---|---|---|
| Small | 2 vCPU | 4 GB | Single instance; AMI + ARI + Realtime over Postgres |
| Medium | 4 vCPU | 8 GB | One node per ~150 concurrent calls |
| Large | 8 vCPU | 16 GB | Pro.Cluster active/active; failover via `FailoverCoordinator` |
| XL | 16 vCPU | 32 GB | One node per ~500 concurrent calls; PJSIP transport tuning |

**Rationale:** Asterisk 22 LTS / 23 Standard documented profile (≥ 4 cores for
PJSIP + concurrent media handling). Cluster node count derives from the sizing
tier table; per-node concurrent-call ceiling is ~500 for Asterisk 22 with PJSIP
+ transcoding off.

---

## Network requirements

| Tier | Sustained bandwidth (in + out) | Notes |
|---|---|---|
| Small | 10 Mbps | G.711 ≈ 87 kbps per call × 50 calls ≈ 4.4 Mbps + signalling overhead |
| Medium | 50 Mbps | Public ingress for SIP/RTP + WebSocket signalling for SignalR |
| Large | 250 Mbps | Dedicated 1 Gbps NIC recommended; intra-cluster latency < 5 ms |
| XL | 1 Gbps | Multi-AZ / multi-DC requires backplane latency budget < 10 ms one-way |

**Rationale:** RTP (G.711 ulaw) ≈ 87 kbps full-duplex per call; WebSocket
SignalR push events average ~200 bytes per merge. Bridge / cluster traffic
adds ~5% overhead above raw call payload.

---

## Bottlenecks observed (provisional)

The following are **expected first-saturation points** based on the architecture
review and the Pro 1.14.0-pro meter catalog. None has been confirmed under load
— each is **(provisional pending S5.1 run)** and will be replaced with the
actual observed bottleneck once `tests/Asterisk.Platform.LoadTests/` runs on
staging hardware.

### 1. JWT validation throughput **(provisional pending S5.1 run)**

**Expected symptom:** Single-node Platform.Api becomes CPU-bound around the
`jwt_issuance_validation` scenario's 2,000 req/s target. The
`AuthLoginErrorRateHigh` (P0) and `JwtValidationLatencyP99High` (P0) alerts in
[`alerts.yml`](alerts.yml) would trip first. The `Asterisk.Platform.Auth.JwtKeyRotation`
meter (`jwt.key.rotation.duration` proxy histogram) is the leading indicator.

**Mitigation already in place:** R5.1 Identity.Redis package exposes a
distributed `IJtiRevocationCache` so nodes scale horizontally without
double-checking JTI revocation against Postgres.

**Scaling path:** add a second Platform.Api node behind a sticky-sessions L7
proxy at the **Medium** tier; the Redis JTI cache handles revocation coherency
across nodes. Per [`slos.md`](slos.md) §1, target p99 ≤ 50 ms — observed
saturation typically arrives ~70% CPU.

### 2. Live queue snapshot writer **(provisional pending S5.1 run)**

**Expected symptom:** Under the `live_queue_snapshot_write` scenario (500
reads/s + writer coalescing at ~5 Hz per `(tenantId, queueName)`),
`PostgresLiveQueueSnapshotStore.UpsertAsync` will compete with `Pro.EventStore`
for Postgres connection pool slots at the **Large** tier. Watch
`Asterisk.Sdk.Pro.Analytics.Live` · `live_queue.snapshots.write_duration_ms`
p99 vs the `LiveQueueWriterErrorWindow` (P2) alert threshold. The
`live_queue.writer.inflight` gauge climbing past 50 indicates the
ResiliencePolicy circuit is approaching open.

**Mitigation already in place:** `analytics.live-queue-writer` ResiliencePolicy
(circuit 5/30s + retry 3/100ms + timeout 5s) per
`Asterisk.Sdk.Pro/docs/architecture.md` § "Pro.Analytics live queue metrics
pipeline".

**Scaling path:** at the **Large** tier and above, configure a **dedicated
Npgsql connection pool slot for the live queue writer** (separate connection
string with `MaxPoolSize` carved out of the global pool) so EventStore + retention
purges + live queue writer don't contend.

### 3. Presence broadcast at scale **(provisional pending S5.1 run)**

**Expected symptom:** The `presence_broadcast` NBomber scenario sustains 1,500
virtual users — at a 3-node cluster, that's ~500 connections per node + cross-
node CRDT merges. `Asterisk.Sdk.Pro.Push.SignalR` · `presence.broadcasts.fanout`
duration will widen, and `Asterisk.Sdk.Pro.Push.Postgres` · `push.postgres.backlog`
will accumulate if the Postgres backplane is the bottleneck. The
`PresenceBacklogGrowing` (P1) alert fires at backlog > 500 with positive growth
over 15min.

**Mitigation already in place:** Pro.Push.SignalR + presence CRDT + opt-in
`SubscribeToAgentPresenceAsync` group filtering (v1.7.1-pro) reduces fan-out.

**Scaling path:** above the **Medium** tier, **switch from
Pro.Cluster.Storage.Postgres to Redis Cluster mode for the push backplane**
(`Asterisk.Sdk.Pro.Push.Redis`) — Postgres LISTEN/NOTIFY caps around 1k
notifications/sec per channel and Redis pub/sub scales horizontally with
cluster shards.

### 4. Postgres connection pool exhaustion **(provisional pending S5.1 run)**

**Expected symptom:** All five Pro pipelines (Dialer, EventStore, Analytics,
AgentAssist, CallAnalytics) compete for the shared Npgsql pool. The
`PgConnectionPoolHigh` (P2) alert in [`alerts.yml`](alerts.yml) fires at
> 80% utilization sustained 30min. The retention purge nightly window (2am UTC)
is the typical first trigger as it holds long-running DELETE LIMIT 10k batches.

**Mitigation already in place:** `RetentionService` runs `DELETE LIMIT 10k`
batches with throttling between batches; `Pro.Storage.Common.Retention` meter
exposes `retention.duration_ms` to spot runaway purges.

**Scaling path:** at **Large** tier, introduce **PgBouncer** between the
Platform.Api + Pro pipelines and the primary Postgres. At **XL**, route read-
heavy queries (Analytics interval snapshots, audit log reads) to a streaming
replica.

---

## Scaling triggers — when to add nodes vs scale up

These triggers cross-reference the canonical alert thresholds in
[`alerts.yml`](alerts.yml). Operators should treat **two consecutive 30-day
windows breaching a P2 trigger** as the signal to upsize the tier.

| Trigger | Source alert | Action |
|---|---|---|
| Platform.Api CPU > 70% sustained 30 min on all nodes | (capacity correlate to `AuthLoginErrorRateHigh`) | Add a Platform.Api node + provision sticky-sessions on the L7 proxy |
| Postgres connection pool > 80% utilization 30 min | `PgConnectionPoolHigh` (P2) | Resize pool first; if Large+ tier introduce PgBouncer |
| Postgres slow-query rate > 1% sustained 30 min | `SlowQueriesHigh` (P2) | Review `pg_stat_statements`; add covering index OR upsize disk IOPS |
| EventStore p99 persist > 200 ms 15 min | `SloBreachQueueIngestion` (P1) | Verify Postgres health, then split EventStore writes onto a dedicated replica |
| Push backplane backlog > 500 + growing 15 min | `PresenceBacklogGrowing` (P1) | Migrate Pro.Push transport to Redis Cluster (Large+ tier) or shard tenants across two backplane instances |
| Resilience circuit `Open` > 5 min | `CircuitBreakerOpen` (P1) | Investigate dependency per [`resilience-runbook.md`](resilience-runbook.md); not directly a capacity signal but often co-occurs with saturation |
| Redis memory > 75% sustained 1 h | `RedisMemoryHigh` (P2) | Tune TTL on JTI cache + push backplane channels; if XL tier, shard Redis Cluster |
| LicenseGuard blocking > 1% pipeline ops 10 min | `LicenseGuardBlockedHigh` (P0) | Not a capacity issue — licensing renewal needed; included here because it presents as missing throughput |

---

## Open questions / future work

The following items are **deferred** beyond R5.4 — capacity planning v2 will
incorporate them once measured data and roadmap commitments justify the work.

- **Multi-region active/active** — deferred to **R6** (per roadmap.md). Current
  capacity envelope assumes single-region deployments; cross-region latency
  budget for the push backplane has not been characterised.
- **Cost-per-tenant model** — needed once multi-tenant SaaS pricing is published.
  Inputs available: `tenant_id` dimension on most Pro meters
  (per R5.2 observability uplift), Postgres rows-per-tenant metrics. Output:
  $/tenant/month based on tier mapping.
- **Auto-scaling triggers (HPA / Karpenter)** — currently all scaling is
  operator-driven. Containerisation in `docker/docker-compose.full.yml` is the
  baseline; Kubernetes Helm charts deferred per
  `Asterisk.Sdk/CLAUDE.md` (R5+ k8s scope).
- **Workforce Management capacity** — Pro.WorkforceManagement deferred to 2.0.0-pro
  per `Asterisk.Sdk.Pro/docs/roadmap.md`; will require revisiting CPU/RAM
  estimates for the forecasting workload (TensorFlow.NET or SQL window functions).
- **AgentAssist external-provider cost ceiling** — STT/LLM calls dominate p95
  latency budget per [`slos.md`](slos.md) §5; capacity v2 should model
  $/conversation by provider tier (Deepgram Aura 2 vs Whisper local vs
  ElevenLabs Flash 2.5).

---

## How to refresh from real data

When the first S5.1 baseline run completes on staging:

1. Open [`docs/operations/load-test-baseline.md`](load-test-baseline.md) and
   read measured rates per scenario.
2. For each tier table:
   - Replace estimated **concurrent calls** / **agents** / **queues** with the
     rate at which the target tier's hardware sustained the SLO targets from
     [`slos.md`](slos.md).
   - Update the **Bottlenecks observed** section with the ACTUAL bottleneck
     observed (CPU? IO? memory pressure? backplane saturation?). Mark each
     entry with the meter and the saturation threshold actually measured.
3. Remove the "v1 provisional" banner at the top of this document.
4. Append a footnote: `Refreshed from S5.1 baseline YYYY-MM-DD by [operator]`.
5. Commit: `docs(operations): refresh capacity planning from S5.1 baseline`.

Subsequent quarterly reviews follow the same procedure with the most recent
baseline run. Every refresh that **upsizes** a tier (relaxes the envelope) also
requires updating the `slos.md` "v2 enterprise (aspirational)" section and the
`alerts.yml` capacity-class thresholds (`PgConnectionPoolHigh`,
`RedisMemoryHigh`, `SlowQueriesHigh`) proportionally, then rerunning
`promtool check rules`.

---

## References

- [ADR-0009](../decisions/0009-slo-baseline-alert-severity-model.md) — SLO baseline + alert severity model
- [`slos.md`](slos.md) — Service-Level Objectives baseline (v1 provisional)
- [`load-test-baseline.md`](load-test-baseline.md) — S5.1 NBomber results template
- [`alerts.yml`](alerts.yml) — Prometheus alert rules derived from these SLOs
- [`alerts-runbook.md`](alerts-runbook.md) — Per-alert what / why / first response
- [`resilience-runbook.md`](resilience-runbook.md) — Resilience meter golden signals
- [`backup-disaster-recovery.md`](backup-disaster-recovery.md) — Backup + DR runbook
- Asterisk.Sdk.Pro `docs/architecture.md` § "Meter catalog" — full instrument inventory
- Asterisk.Sdk.Pro `docs/roadmap.md` — feature deferrals (1.9.x / 2.0.0-pro / R6)
