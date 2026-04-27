# Load Test Baseline — R5.4

**Date:** _YYYY-MM-DD_ (populated on first run)
**Hardware:** _docker-host specs from `lscpu` + `free -h`_
**Repro:** `./scripts/load-test.sh`

## Suite

NBomber 6.1.0 (`tests/Asterisk.Platform.LoadTests/`). Five scenarios run
sequentially against the loadtest stack defined by
`docker/docker-compose.loadtest.yml`. The opt-in project is **not** part of
`Asterisk.Platform.slnx` and is invoked exclusively through the wrapper
script `scripts/load-test.sh`.

## Results

| Scenario | Target rate | Observed p50 | p95 | p99 | Errors |
|---|---|---|---|---|---|
| `jwt_issuance_validation` | 2,000 req/s for 2 min | _ | _ | _ | _ |
| `queue_ingestion` | ~17 req/s (~1,000/min) for 5 min | _ | _ | _ | _ |
| `presence_broadcast` | 1,500 vu sustained for 3 min | _ | _ | _ | _ |
| `live_queue_snapshot_write` | 500 reads/s for 2 min | _ | _ | _ | _ |
| `agent_assist_session_start` | 50 starts/s for 2 min | _ | _ | _ | _ |

(Numbers populated by S5.1 first execution. NBomber writes its own report
under `tests/Asterisk.Platform.LoadTests/load-test-reports/<timestamp>/`
in Markdown + CSV + HTML form — `report.html` is the canonical artifact.)

## Observations

- _TBD post-first-run._ Capture: bottlenecks, saturation points, error-rate
  inflections, observed memory + CPU trends from the platform-api container.
- _Recommendations for SLO targets feed into S5.2 (`docs/operations/slos.md`)._
- _Recommendations for capacity tiers feed into S5.7 (`docs/operations/capacity.md`)._

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
