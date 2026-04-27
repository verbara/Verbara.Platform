# Pumba Chaos Experiments — R5.5 Phase L

Reproducible Docker chaos engineering experiments using
[Pumba](https://github.com/alexei-led/pumba). These experiments populate
the local-staging-only path of `docs/operations/chaos-test-report-local.md`
during R5.5 Phase C-L. The K8s-native variant (Chaos Mesh) of the same 10
behaviors lives at `tests/chaos-experiments/chaos-mesh/` and is authored
during R5.5 A.7 (after Phase 0LK lands).

## Prerequisite

```bash
# Pumba binary install — pumba 0.10+
[ -f /usr/local/bin/pumba ] || curl -sL \
  https://github.com/alexei-led/pumba/releases/download/0.10.0/pumba_linux_amd64 \
  -o /tmp/pumba && chmod +x /tmp/pumba && sudo mv /tmp/pumba /usr/local/bin/pumba
pumba --version
```

The experiments use Pumba's regex matcher (`re2:<name>`) to target
containers by name; this works against the
`docker-<service>-1` naming convention used by `docker-compose.full.yml`.

## Experiments

| #  | File                          | Behavior                                   | Primary expected alert(s)             |
|----|-------------------------------|--------------------------------------------|---------------------------------------|
| 01 | `01-pg-pause-30s.sh`          | Pause Postgres 30 s                        | None to P1 transient                  |
| 02 | `02-pg-kill-restart.sh`       | SIGKILL Postgres + auto-recover            | PlatformApiUnavailable (transient)    |
| 03 | `03-redis-pause-30s.sh`       | Pause Redis 30 s (skipped if not running)  | RedisMemoryHigh unaffected            |
| 04 | `04-redis-kill-restart.sh`    | SIGKILL Redis + auto-recover               | None expected (graceful degrade)      |
| 05 | `05-asterisk-crash.sh`        | SIGKILL Asterisk + auto-recover            | HealthCheckUnhealthy P1               |
| 06 | `06-platform-api-crash.sh`    | SIGKILL Platform.Api + auto-recover        | PlatformApiUnavailable P0             |
| 07 | `07-network-partition-pg.sh`  | 100% packet loss to Postgres for 60 s      | CircuitBreakerOpen P1                 |
| 08 | `08-network-delay-pg.sh`      | 200 ms latency on Postgres for 60 s        | SloBreachQueueIngestion P1            |
| 09 | `09-bandwidth-limit-rtp.sh`   | Asterisk → 200 Kbps for 60 s               | BlackboxJourneyDown P0 may flap       |
| 10 | `10-packet-loss-rtp.sh`       | 5% packet loss on Asterisk for 60 s        | RTP packet-loss meter elevated        |

Each script is self-contained (overridable via `DURATION`, `TARGET`,
`IFACE`, etc. env vars) so an operator can re-run any single experiment
with custom knobs.

## Run all (sequential, with snapshots)

```bash
./scripts/chaos-test.sh
```

`scripts/chaos-test.sh` orchestrates the 10 experiments sequentially with
a 30 s recovery window between each. Per-experiment Postgres `pg_dumpall`
+ Redis `SAVE` + `dump.rdb` copy are taken before AND after each run,
landing under `chaos-snapshots/<timestamp>/`. The wrapper never aborts
on a single experiment's non-zero exit (some experiments are expected
to disrupt subsequent commands during recovery).

## Phase coverage

| Phase | What runs |
|-------|-----------|
| **C-L stress + chaos** | All 10 experiments while NBomber + SIPp run @ Phase B-L baseline rates. Captures recovery time + alert correctness on each behavior. |
| **D-L 24h soak** | Experiments 01 / 03 / 08 (the recoverable ones) at random intervals to expose memory leaks / connection drift. |
| **F.6 data sheet** | Recovery-time observations + alert-correctness scorecard fed into the closure deliverable. |

## Authoring conventions

- All scripts begin with `set -euo pipefail` and document the validates /
  expected behavior in the comment header.
- Targets default to `re2:<service>` so the experiments work against any
  compose project regardless of project-prefix variation.
- Optional services (e.g. Redis) skip cleanly when not running rather
  than failing the suite.
- Disruption scripts never block on user input — Phase C-L can chain
  them via `chaos-test.sh` without supervision.
