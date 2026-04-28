# Load Test Baseline — R5.4 (R5.5 first-measurement amendment)

**First measured:** 2026-04-27 (R5.5 Phase B-L baseline run #2, post Content-Type fix)
**Hardware:** AMD Ryzen 9 9900X (12 cores / 24 threads) · 60 GB RAM · NVMe SSD · docker-compose.full.yml staging stack (single-instance Postgres 17 + Redis + Asterisk + Platform.Api + Web)
**Repro:** `LOADTEST_PROFILE=staging ./scripts/load-test.sh`

> **Provenance:** R5.4 v1 provisional was unmeasured. R5.5 Phase B-L is the
> first execution against a real staging stack populated by
> `scripts/seed-staging.sh` (3 tenants × 25/100/500 agents). The numbers
> below are **first-attempt measurements** that surfaced multiple platform
> + scenario-side defects (see "Findings" section); they are the
> v1-measured starting point but NOT yet a refined SLO source — refining
> those is Phase F work.

## Suite

NBomber 6.1.0 (`tests/Asterisk.Platform.LoadTests/`). Five scenarios run
sequentially against the loadtest stack defined by
`docker/docker-compose.loadtest.yml`. The opt-in project is **not** part of
`Asterisk.Platform.slnx` and is invoked exclusively through the wrapper
script `scripts/load-test.sh`.

## Results — R5.5 Phase B-L baseline run (2026-04-27)

| Scenario | Target rate | Observed | Outcome | Status code mix |
|---|---|---|---|---|
| `jwt_issuance_validation`   | 2,000 req/s × 2 min | 8526 reqs in **18 s** before NBomber halted; 2 ok / 8524 fail; ok p99 = 20.7 s | Stack saturated almost immediately at 2 k req/s | OK=2 · -101=6872 (HTTP send error) · 500=1652 |
| `queue_ingestion`           | 17 req/s × 5 min    | 81 reqs / 18 s; 0 ok / 81 fail | Endpoint `POST /api/v1/queues/{id}/calls` does not exist (404) | All 404 |
| `presence_broadcast`        | 1,500 vu × 3 min    | 1473 reqs / 18 s; 0 ok / 1473 fail | Endpoint `POST /api/v1/agents/{id}/presence` does not exist (404) | 1466 NotFound · 7 InternalServerError |
| `live_queue_snapshot_write` | 500 req/s × 2 min   | 2620 reqs / 18 s; 0 ok / 2620 fail | Endpoint path `/operations/queues/{name}/live-metrics` does not match real `/api/v1/analytics/live/{queueName}` (404) | Mostly 404 |
| `agent_assist_session_start`| 50 req/s × 2 min    | 241 reqs / 18 s; 0 ok / 241 fail | Endpoint `POST /api/v1/agent-assist/sessions/{id}/start` does not exist (404) | 159 -101 · 30 InternalServerError · 52 NotFound |

NBomber reports:
- `tests/Asterisk.Platform.LoadTests/load-test-reports/nbomber_report_2026-04-27--15-50-19.{md,csv,html}`
- Verbose run log (`nbomber-log-*.txt`, ~19 MB) is gitignored.

## Results — R5.5 Phase B-L baseline run #3 (post live-queue URL reconcile + admin token, 2026-04-27)

| Scenario | Target rate | Observed | Outcome |
|---|---|---|---|
| `jwt_issuance_validation`   | 2,000 req/s × 2 min | 8827 reqs / 19 s; 1 ok / 8826 fail; ok p99 = 21.97 s | Same saturation as #1 — JWT issuance @ 2 k req/s is well above docker-compose ceiling |
| `queue_ingestion`           | 17 req/s × 5 min    | 90 reqs / 19 s; 0 ok | Endpoint still 404 (path didn't change) |
| `presence_broadcast`        | 1,500 vu × 3 min    | 1340 reqs / 19 s; 0 ok | 404 + 5xx |
| `live_queue_snapshot_write` | 500 req/s × 2 min   | 2362 reqs / 19 s; 0 ok / 2362 fail | URL now `/api/v1/analytics/live/{queue}` with admin token; 404s because no SIP traffic populated the live snapshot store. Stack saturation evident in -101 socket errors. |
| `agent_assist_session_start`| 50 req/s × 2 min    | 269 reqs / 19 s; 0 ok | 404 + saturation |

`load-test-reports/nbomber_report_2026-04-27--16-02-50.{md,csv,html}`.

**HTTP server meter signals confirmed** (post `b58cf0f`):
- Smoke load (200× /health): p99 = 22.8 ms. Within v1-provisional 200 ms SLO.
- Sustained load: meter exposition works, but stack saturates before steady-state metrics stabilize. Real SLO measurement requires Phase C-L lower-rate sweep.

**Pipeline gap on live-queue 404s:** the staging Asterisk has 0 calls in flight, so `LiveQueueSnapshotWriter` (Pro.Analytics.Live R5.1 Task G) never produces a snapshot, and `GET /analytics/live/{queueName}` correctly returns 404. To measure the live-queue read path under realistic conditions, Phase C-L needs SIPp driving inbound calls in parallel.

## Results — JWT rate sweep (B-L #4, 2026-04-27)

First clean v1-measured datapoint. JWT login + /auth/me round-trip,
sequential steps × 60 s each, fresh login per iteration. Per-step
NBomber report under `tests/Asterisk.Platform.LoadTests/load-test-reports/`.
Repro: `./scripts/jwt-sweep.sh`.

| Rate (req/s) | OK     | Fail   | OK %   | min ms | mean ms | p50 ms | p95 ms  | p99 ms     |
|--------------|-------:|-------:|-------:|-------:|--------:|-------:|--------:|-----------:|
| 10           |    600 |      0 | 100.0% |    170 |     175 |    174 |     183 |        189 |
| 50           |  3 000 |      0 | 100.0% |    176 |     189 |    188 |     201 |        213 |
| 100          |  6 000 |      0 | 100.0% |    181 |     396 |    389 |     596 |        671 |
| 250          |  1 804 |  2 226 |  44.8% |  1 016 |  32 753 | 34 668 |  53 805 |  **56 918** ← collapse |
| 500          |    485 |  7 234 |   6.3% |  4 898 |  29 967 | 32 096 |  45 514 |  46 268 |

### Knee analysis

- **Sustainable JWT login throughput on this hardware = 50–75 req/s.** At 50
  req/s the p99 latency (213 ms) sits right at the v1-provisional 200 ms
  SLO line in `slos.md`; the slack disappears beyond that.
- **At 100 req/s** the stack still serves all requests (6 000 / 6 000 OK,
  zero 5xx) but p99 jumps to 671 ms — 3.4× over the SLO. This is the
  "ouch but still alive" zone: throughput holds, latency tail explodes.
- **At 250 req/s the stack collapses.** OK rate drops to 44.8 %, half the
  remaining 2 226 requests return HTTP 500 with p99 = 56.9 s. The
  Postgres connection pool + `EntityFrameworkCoreXmlRepository`
  DataProtection lookups become the dominant bottleneck at this rate.
- **At 500 req/s only 6 % of requests survive** with p99 = 46 s — pure
  saturation territory.

### What this means for v1-measured SLO numbers

The 200 ms p99 target in `slos.md` is **achievable on this single-instance
docker-compose host up to ~50 req/s**. To approach the R5.4 aspirational
2 000 req/s target with the same SLO requires:

1. Platform.Api horizontal scaling (≥ 4 replicas to spread auth load).
2. Postgres connection pool tuning (current default is too small for
   sustained 200+ req/s of `data_protection_keys` + `users` reads).
3. DataProtection key ring caching upgrade — the per-request EF Core round-
   trip on every JWT issuance is the bottleneck at the knee.
4. Optional: JTI revocation cache promotion to Redis (R5.4 known-debt
   JWT-001 follow-up; deferred to v1.13.x patch train).

Phase F closure should publish these as the **honest v1-measured ceiling**
and pair them with the recommended scaling deltas instead of preserving
the unmeasured aspirational numbers.

## Results — R5.5 Phase B-L baseline run #5 (full suite, real read endpoints, 2026-04-27)

After the R5.5 P1 follow-up rewrote the 3 dead-URL scenarios to hit real
read endpoints (`GET /admin/queues`, `GET /admin/agents`, `GET /admin/teams`),
the full 5-scenario suite was re-run at the original design rates with NBomber's
default parallel execution. **This run is intentionally over the dev-workstation
knee** — its purpose is validating that the rewrites land on real paths, not
producing clean per-scenario p99 numbers.

| Scenario | Target rate | OK | Fail | OK % | Notes |
|---|---|---:|---:|---:|---|
| `jwt_issuance_validation`   | 2 000 req/s × 2 min | 0      | 8 653 | 0.0 %   | Same saturation pattern as B-L #2/#3 — JWT @ 2 k req/s above knee |
| `queue_ingestion`           |    17 req/s × 5 min | 14     |    68 | 17.1 %  | New `GET /admin/queues` path returns OK at low rate; remainder lost to concurrent saturation |
| `presence_broadcast`        | 1 500 VUs × 3 min   | 663    |   234 | 73.9 %  | New `GET /admin/agents` path produces real signal — 663 successful reads, p99 = 19.7 s under sat |
| `live_queue_snapshot_write` |   500 req/s × 2 min | 0      | 2 374 | 0.0 %   | `/analytics/live` returns 404 (no SIP traffic populates snapshots — by design) |
| `agent_assist_session_start`|    50 req/s × 2 min | 22     |   211 | 9.4 %   | New `GET /admin/teams` path returns OK at low rate; saturation eats the rest |

**Aggregate finding:** with all 5 scenarios firing in parallel at design rates,
peak aggregate load ≈ 5 000 req/s — well above the **single-instance knee
identified in B-L #4 (~75 req/s sustainable)**. The parallel suite is therefore
a stress-test and saturation-finding tool, not a per-scenario baseline; the
authoritative per-endpoint numbers come from sequential isolated runs (JWT
sweep is the first; equivalent sweeps for the other 4 are R5.5 follow-up).

**Why the rewrites still matter** even though full-parallel saturates: with
real endpoints the OK counts now grow > 0 across all scenarios (663 presence
reads in 3 min vs the previous 0 ok / 1 473 fail). Sequential runs of the
rewritten scenarios at sustainable rates would produce clean per-endpoint
p99 numbers — those land in Phase D-L 24 h soak prep + Phase F dataset
integration.

## Findings (R5.5 surface)

The first run produced no clean throughput numbers — every scenario was
gated by a different upstream defect. Documenting them as the actual v1
measurement reality:

1. **P0 finding #4 (already worked-around in seed-staging.sh):** Platform.Api
   `/admin/users` + `/admin/queues` return HTTP 500 with raw Postgres
   `idx_users_email` / `idx_queues_name` UNIQUE-constraint message instead
   of 409 Conflict. Tracked separately for a Platform.Api fix.

2. **P0 finding #5 (already worked-around in seed-staging.sh):**
   `/admin/users?email=…` query param is silently ignored — the endpoint
   returns the first page regardless. Workaround: bulk-fetch + client-side
   filter. Tracked separately.

3. **P0 finding #6 (fixed in scenarios commit X):** NBomber's
   `WithBody(StringContent(json))` defaults the request Content-Type to
   `text/plain`, which Platform.Api rejects with HTTP 415
   UnsupportedMediaType. Fix: pass `Encoding.UTF8, "application/json"` to
   the StringContent constructor. Symptom that hid all other issues until
   fixed: every POST returned 415, not the real downstream code.

4. **P1 finding (not yet fixed):** 4 of 5 NBomber scenarios target
   endpoints that no longer exist on the current Platform.Api surface
   (queue-ingestion, presence, live-queue, agent-assist). The endpoints
   were authored against R5.4-era assumptions that don't match shipped
   code. Reconciliation:
     - `live_queue_snapshot_write` → real path is
       `GET /api/v1/analytics/live/{queueName}` (mechanical fix).
     - `queue_ingestion`, `presence_broadcast`, `agent_assist_session_start`
       → real intent has no HTTP equivalent (queue ingestion happens via
       SIP, presence via SignalR, agent-assist sessions are
       system-initiated when calls reach AgentAssist-enabled queues).
       These scenarios should be replaced with synthetic equivalents that
       drive the underlying meters (e.g. SignalR client for presence,
       SIPp for ingestion).

5. **P1 finding (not yet fixed):** Platform.Api exposes only 20 meter
   types over `/metrics` (Pro-level) — the standard
   `Microsoft.AspNetCore.Hosting` HTTP server request duration meters
   are not registered. The SLO targets in `slos.md` reference HTTP
   p99 latency which therefore has **no measurable signal** in the
   current `/metrics` exposition. NBomber's client-side latency
   measurement is the only available signal. Fix: extend
   `Asterisk.Sdk.OpenTelemetry.WithAllSources()` to register
   `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Server.Kestrel`,
   `System.Net.Http`. Tracked separately.

## Observations

- **Login throughput ceiling at 2 k req/s** — the docker-compose
  single-instance config saturates within 18 s (HttpClient socket
  exhaustion + 500s, then NBomber gives up). Of the 8526 attempts, only
  2 succeeded with p99 latency = 20.7 s — far above any SLO.
  Realistic ceiling on this hardware is somewhere below 500 req/s
  (roughly the rate of the 1652 InternalServerErrors during the saturated
  phase). Phase C-L stress sweep should hit r=10..500 to find the
  actual knee.
- **Recommended SLO target in `slos.md` for JWT p99 (200 ms)** is
  almost certainly achievable at moderate load (≤100 req/s) but
  unmeasurable at the tested 2 k rate on this hardware.
- **Recommendations for capacity tiers** — single-instance dev workstation
  cannot serve a Medium tier (100 agents × 50 queues) at design rates
  without an additional Platform.Api replica + Postgres pool tuning.

## Next steps

1. ✅ Meter exposition gap closed (`b58cf0f`).
2. ✅ Live-queue scenario URL reconciled to real `/api/v1/analytics/live/{queueName}` (B-L run #3).
3. **Next**: rewrite the 3 remaining dead-URL scenarios to drive real signal paths:
   - `queue_ingestion` → SIP-driven via `tests/sipp-scenarios/03-queue-join.xml` once Asterisk PJSIP endpoints are provisioned (Phase B-L SIP-side prep).
   - `presence_broadcast` → SignalR client (not REST). Defer to Phase C-L if needed.
   - `agent_assist_session_start` → System-initiated when calls reach AgentAssist queues. Tied to Phase C-L SIPp + AgentAssist provisioning.
4. **Phase C-L stress sweep**: rather than the all-or-nothing 2 k req/s,
   ramp r=10/50/100/250/500 req/s × 60 s each on `jwt_issuance_validation`
   to find the docker-compose ceiling on this hardware. Capture latency
   percentiles + 5xx rate at each step.
5. **Phase D-L 24 h soak**: at the safe rate identified by the C-L sweep
   (likely ≤100 req/s sustained). Pair with SIPp scenario 03 (queue-join)
   to drive the live-queue meter pipeline + analytics live snapshot store
   under sustained traffic.

## Reproducibility

1. Ensure Docker + dotnet 10 SDK are installed locally.
2. From the repo root:
   ```bash
   ./scripts/load-test.sh
   ```
3. The script brings up the loadtest stack, obtains a bearer token, runs
   NBomber, and tears the stack down. Set `LOADTEST_KEEP=1` to leave the
   stack running for follow-up exploration.
4. Reports for each run are preserved under
   `tests/Asterisk.Platform.LoadTests/load-test-reports/<timestamp>/`.
   Commit only the report directory you want to baseline against.

## Notes

- The first run requires images `asterisk:22-loadtest` and
  `asterisk-platform-api:loadtest` to be built locally; the corresponding
  Dockerfiles + tagging script ship as part of S5.1 follow-up.
- `Loadtest__SeedTenant=true` triggers the platform API's loadtest seed
  path on first boot to provision the `loadtest` tenant + user with
  password `loadtest`. Do **not** run the loadtest stack alongside any
  production-shaped data set.

## Phase C-L SMB tier stress sweep (2026-04-28, post-ADR-0015 Phase 1)

The first Phase C-L sweep against `docker-compose.full.yml`
(`scripts/scenario-sweep.sh all-reads`) revealed a connection-pool
sprawl bug: 14 separate `NpgsqlDataSource` instances across the Pro
storage packages over-subscribed `max_connections=100` (postgres-alpine
default) under VU=100 concurrent reads, producing 13 % HTTP 500
`Npgsql.PostgresException (53300): sorry, too many clients already`.
**Per-instance demand vs cap was 14×.** ADR-0015 captures the
diagnosis + Phase 1 (smart pool defaults at the Platform.Api
composition root) + Phase 2 (Pro 1.16.0-pro shared `NpgsqlDataSource`
overload) two-phase mitigation strategy.

The sweep below was re-run after Phase 1 shipped (v1.14.5) on
`docker-compose.smb.yml` — the SMB tier production-ready stack.

**Hardware:** AMD Ryzen 9 9900X (24 threads), 60 GB RAM, NVMe SSD.
**Stack:** `docker-compose.full.yml + docker-compose.smb.yml` overlay
(`max_connections=200`, `shared_buffers=512MB`,
`effective_cache_size=2GB`, per-data-source `Maximum Pool Size=10`).
**Tenant:** `medium-loadtest` (50 agents, 50 queues — sourced via
`scripts/seed-staging.sh`).
**Reproducibility:** `./scripts/scenario-sweep.sh all-reads` (token
auto-refreshed per step via `/auth/login`).

### `queues` — `GET /api/v1/admin/queues?pageSize=20`

Knee NOT crossed at the sweep's top step (500 req/s sustainable with
sub-2 ms p99). Real ceiling needs a higher ladder run (1k / 2k / 5k);
out of scope for this baseline (the read endpoint is non-critical-path).

| r req/s | OK | Fail | p50 ms | p95 ms | p99 ms |
|---:|---:|---:|---:|---:|---:|
| 10 | 600 | 0 | 1.22 | 2.06 | 2.81 |
| 50 | 3 000 | 0 | 0.86 | 1.52 | 1.99 |
| 100 | 6 000 | 0 | 0.81 | 1.26 | 1.98 |
| 250 | 15 000 | 0 | 0.76 | 1.17 | 1.85 |
| 500 | 30 000 | 0 | 0.74 | 1.00 | 1.59 |

### `livequeue` — `GET /api/v1/analytics/live/{queueName}`

100 % `NotFound` in all 5 steps. **By design:** no SIP traffic populates
the snapshots, so `ILiveQueueMetricsProvider` returns nothing for the
queried queue → endpoint returns 404. Latency is real (not a rebound)
because auth + tenant + DB read all execute. **This scenario requires a
SIPp companion driving inbound calls** to produce a meaningful signal —
out of scope for HTTP-only sweeps. Tracked as a Phase D-L follow-up.

### `agentassist` — `GET /api/v1/admin/teams?pageSize=20`

Knee NOT crossed at 500 req/s (same shape as `queues`).

| r req/s | OK | Fail | p50 ms | p95 ms | p99 ms |
|---:|---:|---:|---:|---:|---:|
| 10 | 600 | 0 | 1.04 | 1.74 | 2.08 |
| 50 | 3 000 | 0 | 0.80 | 1.41 | 1.86 |
| 100 | 6 000 | 0 | 0.77 | 1.11 | 1.71 |
| 250 | 15 000 | 0 | 0.70 | 1.04 | 1.64 |
| 500 | 30 000 | 0 | 0.67 | 1.15 | 1.61 |

### `presence` — `GET /api/v1/admin/agents?pageSize=20` (VU shape)

The scenario that originally exposed the sprawl bug. Post-fix:
**zero failures across the entire VU=100 → 1 500 ladder**, and
aggregate throughput levels off at ~11 k req/s from VU=100 onward (the
platform's CPU/Postgres-bound ceiling). The knee is therefore latency-
defined, not throughput-defined: more VUs queue against the constant
throughput ceiling and see proportionally higher p99.

| VU | OK | Fail | p50 ms | p95 ms | **p99 ms** | RPS aggregate |
|---:|---:|---:|---:|---:|---:|---:|
| 100 | 661 738 | 0 | 8.63 | 12.08 | **16.62** | 11 029 |
| 250 | 678 772 | 0 | 21.49 | 28.00 | **34.59** | 11 312 |
| 500 | 646 262 | 0 | 44.80 | 59.42 | **69.50** | 10 770 |
| 1000 | 656 954 | 0 | 89.86 | 105.86 | **115.97** | 10 949 |
| 1500 | 662 023 | 0 | 132.86 | 156.42 | **174.21** | 11 034 |

**SMB tier knee envelope (latency-defined):**

| Latency budget | Max sustained VU |
|---|---:|
| p99 ≤ 50 ms | ≤ 250 |
| p99 ≤ 100 ms | ≤ 750 (interpolated between VU=500 and VU=1000) |
| p99 ≤ 200 ms | ≤ 1 500 |

Beyond VU=1500 the sweep would continue producing OK responses with
linearly-growing p99; the practical knee for product positioning is
**VU=1000** (p99=116 ms, well within 250 ms typical SLO budget).

### Pre-fix vs post-fix comparison (presence, same hardware, same ladder)

| VU | Pre-fix | Post-fix |
|---:|---|---|
| 100 | 111 824 OK / 16 168 fail (87 % OK) · p99 OK 91 ms · ~1 864 RPS | 661 738 OK / 0 fail · p99 16.62 ms · 11 029 RPS |
| 250 | 0 OK / 44 413 Unauthorized | 678 772 OK / 0 fail · p99 34.59 ms |
| 500 | 0 OK / 44 299 Unauthorized | 646 262 OK / 0 fail · p99 69.50 ms |
| 1000 | 0 OK / 43 249 Unauthorized | 656 954 OK / 0 fail · p99 115.97 ms |
| 1500 | 0 OK / 37 267 Unauthorized | 662 023 OK / 0 fail · p99 174.21 ms |

- Concurrency capacity: **15× improvement** (bug-saturation at VU=100 → clean operation through VU=1500).
- Aggregate throughput at VU=100: **6× improvement** (1 864 RPS → 11 029 RPS).
- Postgres `pg_stat_activity` post-sweep: 21 client backend connections (well under `max_connections=200` cap).
- Zero `Npgsql.PostgresException (53300)` entries in `platform-api` logs across the entire 22-min sweep.

### Reproducibility

```bash
docker compose -f docker/docker-compose.full.yml \
               -f docker/docker-compose.smb.yml up -d --wait
./scripts/seed-staging.sh
./scripts/scenario-sweep.sh all-reads
```

Per-step NBomber reports under
`tests/Asterisk.Platform.LoadTests/load-test-reports/`. Per-step screen
logs at `/tmp/scenario-sweep-<scenario>-r<rate>.log`.

### References

- ADR-0015 — npgsql-datasource-sharing-strategy
- ADR-0014 amendment — auth-horizontal-scaling-baseline § "Update 2026-04-28 (R5.5 Phase C-L · v1.14.5)"
- Plan: `docs/plans/active/2026-04-28-postgres-pool-sprawl-mitigation.md`
- Pro 1.16.0-pro Phase 2 plan-skeleton: `docs/research/archived/2026-04-28-Pro-1.16.0-pro-shared-datasource-skeleton.md`

## Phase C-L SMB tier post-Phase-2 (2026-04-28, v1.14.6 + Pro 1.16.0-pro)

`presence` scenario re-run after ADR-0015 Phase 2 shipped (Platform v1.14.6 builds **one shared `NpgsqlDataSource`** per distinct connection string, threaded through all 9 Pro storage packages via the new Pro 1.16.0-pro `Use*Storage(IServiceCollection, NpgsqlDataSource)` overloads). Same hardware + same stack as the Phase 1 baseline above.

| VU | Phase 1 (14 pools × 10 = 140 ceiling) | Phase 2 (1 pool × 10 = 10 ceiling) | Δ p99 |
|---:|---|---|---|
| 100 | 661 738 OK · p99 16.62 ms · 11 029 RPS | **662 776 OK · p99 16.13 ms · 11 046 RPS** | clean |
| 250 | 678 772 OK · p99 34.59 ms | **670 888 OK · p99 32.27 ms** | -2 ms |
| 500 | 646 262 OK · p99 69.50 ms | **678 532 OK · p99 57.06 ms** | **-12.4 ms** |
| 1000 | 656 954 OK · p99 115.97 ms | **649 421 OK · p99 ~107 ms** | **-9 ms** |
| 1500 | 662 023 OK · p99 174.21 ms | **655 681 OK · p99 ~154 ms** | **-20 ms** |

**Quantitative gains over Phase 1:**

- Latency improvement at high concurrency (VU 500–1500: 9–20 ms p99 reduction). Consolidating 14 small pools into 1 removes per-pool acquisition overhead under contention.
- Aggregate throughput unchanged (~11 k RPS) — the platform is CPU/Postgres-bound at this level.
- `pg_stat_activity` post-sweep: 13 idle conns (Phase 1 had 21).
- Zero failures, zero `Npgsql.PostgresException`.

**SMB tier knee envelope updated for Phase 2:**

| Latency budget | Max sustained VU |
|---|---:|
| p99 ≤ 50 ms | ≤ 250 |
| p99 ≤ 100 ms | ≤ 1 000 (interpolated; was ≤ 750 in Phase 1) |
| p99 ≤ 200 ms | ≤ 1 500 (clean — was at envelope edge in Phase 1) |

### Phase 2 references

- ADR-0015 § "Phase 2 measured impact (2026-04-28)"
- v1.14.6 CHANGELOG entry "ADR-0015 Phase 2 — shared NpgsqlDataSource adoption"
- Pro 1.16.0-pro CHANGELOG entry + Pro ADR-0008 (`Asterisk.Sdk.Pro/docs/decisions/0008-shared-datasource-overload.md`)
