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
