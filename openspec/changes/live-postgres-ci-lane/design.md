# Design — live-postgres-ci-lane

Backlog change: finalized at apply time.

- **Substrate:** Testcontainers (Platform's existing in-repo pattern, `PostgresRbacFixture.cs`;
  also Sdk/ADR-0005) over a `docker compose` service — each test fixture already owns its container
  lifecycle, so a GitHub Actions job just needs Docker-in-Docker (already available on
  `ubuntu-latest`), not a new compose file.
- **Scope:** run exactly the two suites `ci.yml` currently excludes —
  `Storage.Postgres.Tests` and `Identity.Redis.Tests` (`ci.yml:83`) — as their own job, not a
  filter flip on the existing `build-and-test` job (keeps the fast unit lane fast).
- **Wait strategy:** `UntilCommandIsCompleted("pg_isready", ...)`, matching
  `PostgresRbacFixture.cs:29-30` and the Sdk ADR-0005 warning against `/proc/net/tcp` polling
  (empty on GitHub Actions runners, hangs ~30 min).
- **Required-check status:** deferred to grounding at apply time (see spec requirement) — options
  are (a) required check from day one, (b) informational for N releases then promoted, mirroring
  how the coverage-ratchet gate (`ci.yml` `coverage` job) was introduced non-blocking before
  becoming load-bearing. Do not pre-decide here.
- **Out of scope:** does not change `Storage.Postgres.Tests`/`Identity.Redis.Tests` themselves, only
  where/whether they run in CI. Does not add new test coverage — the dunning/Art.17 bugs already
  have their own fixes shipped; this closes the CI gap that let them ship undetected.
- **References:** Platform `.github/workflows/ci.yml` (existing exclusion + rationale comment),
  `tests/Verbara.Platform.Storage.Postgres.Tests/Seeds/PostgresRbacFixture.cs`, Sdk
  `docs/decisions/0005-testcontainers-for-integration.md`, verbara-meta ADR-0004 (test-determinism
  fences — apply to any new container-backed test added under this lane).

## Grounding-resolution note (2026-07-05, apply time)

**Discovery (task 1.1):** No trait/category marks these suites — discovery is by PROJECT, matching
`ci.yml`'s own filter comment. `Storage.Postgres.Tests` = 178 tests across 14 Testcontainers-Postgres
fixture classes (13 own containers + `ServiceCollectionExtensionsTests`, container-free);
`Identity.Redis.Tests` = 34 tests across ~8 Testcontainers-Redis fixture classes. Both build clean,
0 warnings, under the repo's `TreatWarningsAsErrors`.

**Finding #1 (real, not a test-logic bug):** the two suites do NOT reliably pass locally when run
together as a single invocation. Repeated local runs surfaced a real, reproducible flake — always
the same signature: `Npgsql.NpgsqlException: Exception while reading from stream` /
`Connection reset by peer`, always inside a fixture's `InitializeAsync()`, always on the FIRST
`NpgsqlConnection.OpenAsync()` immediately after the `pg_isready` wait strategy reports success.
Root-caused (not just observed): the official `postgres:16-alpine` entrypoint runs a bootstrap
`postgres` process to apply init scripts, stops it, then starts the real long-running server — and
the container's published TCP port (what the host-side Npgsql client actually dials) can have a
brief propagation gap around that restart even though `pg_isready`, which Testcontainers runs
INSIDE the container over the Unix socket, already reported "accepting connections". This is a
known class of race with the official Postgres image + `pg_isready`-only wait strategies; it is
distinct from (and additive to) plain container-start concurrency:

- Isolating a single fixture class (30 tests) alone: 5/5 clean runs.
- One full `Storage.Postgres.Tests` project run alone, with xUnit collection parallelism DISABLED
  (`xunit.runner.json`, `parallelizeTestCollections: false` — added to both projects, since xUnit's
  default parallelizes test collections and each of the ~13/~8 fixture classes owns its own
  container): 178/178 clean once.
- But repeated project-alone runs are NOT uniformly clean even serialized: 3 consecutive full
  `Storage.Postgres.Tests` runs (serialized, otherwise idle host) surfaced 34, 42, and 94 failures
  out of 178 respectively — i.e. serializing collections reduces concurrent-container pressure but
  does **not** eliminate the race, because it is fundamentally a per-container
  bootstrap-restart timing issue, not (only) a resource-contention issue.
- `Identity.Redis.Tests` was 100% clean across every configuration tried (multiple full runs, 34/34
  each) — Redis's official image has no analogous bootstrap-then-restart cycle, so its
  `redis-cli ping` wait strategy doesn't hit this race.

**Scope decision:** the proper fix is a bounded connect-retry (or swapping to the
`Testcontainers.PostgreSql` module's `PostgreSqlBuilder`, whose wait strategy additionally watches
for the container's SECOND "database system is ready to accept connections" log line — the
industry-standard mitigation for this exact image behavior) in EACH of the ~13 Postgres fixture
classes. That is genuine test-authorship surface, not CI wiring, and this change's own "Out of
scope" line above is explicit: "does not change `Storage.Postgres.Tests`/`Identity.Redis.Tests`
themselves, only where/whether they run in CI." Rewriting 13 fixtures' connection-open paths is
larger than "trivial drift" and is deliberately left to a follow-up rather than expanded into this
backlog change's scope.

**Applied within scope:** `xunit.runner.json` (`parallelizeTestCollections: false`) was added to
both `Storage.Postgres.Tests` and `Identity.Redis.Tests` — this is CI-lane-shaped (execution
config, zero test-logic changes), reduces concurrent-container pressure (a real contributing
factor whenever the lane's job runs alongside other load), and is a strict improvement with no
downside beyond wall-clock time (both projects still complete in well under the job's timeout even
serialized). The CI job also runs the two projects as separate, sequential `dotnet test` steps
(not one combined invocation) so their containers never compete with each other either.

**Required-check decision (task 1.2): report-only, NOT required, from day one.** The evidence above
is unambiguous: this lane will intermittently fail PRs for reasons that have nothing to do with the
PR's own changes, which is precisely the failure mode a required check must not have (it trains
contributors to click "re-run" without looking, defeating the check's purpose). Options (a)
required-from-day-one and (b) informational-then-promoted from the proposal's Architectural Risk
section — evidence supports (b). `continue-on-error: true` is set on both `Storage.Postgres.Tests`
and `Identity.Redis.Tests` steps in the `live-db-tests` job; the job still runs on every PR +
merge_group and its results stay visible in the Checks tab, so the signal is not lost, just
non-blocking.

**Promotion trigger:** promote `live-db-tests` to a required check once EITHER (a) a fixture-level
fix for the Postgres bootstrap-restart race lands (bounded retry or `Testcontainers.PostgreSql`
adoption across the ~13 fixtures) and the lane then runs green for a few consecutive real PRs, or
(b) the flake rate is independently re-measured as negligible on actual GitHub Actions runners
(this grounding ran on a shared local dev box with variable background load from concurrent
agent/orchestration sessions — a real risk factor of its own, but the root cause identified here
reproduced even in single-process isolation on that box, so it is not solely an artifact of that
noise). Track the fixture-retry fix as an explicit OpenSpec follow-up change once this one ships.
