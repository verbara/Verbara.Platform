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
