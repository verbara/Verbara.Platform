# live-db-ci-lane Specification

## Purpose
Closes the CI gap where persistence logic depending on real Postgres/Redis semantics — column
defaults, `ON CONFLICT`, migration-applied schema, jsonb operators — shipped covered only by the
`Storage.InMemory` mirror, which can silently diverge from what actually persists. Runs
`Storage.Postgres.Tests` and `Identity.Redis.Tests` against real containers in a lane distinct from
the existing unit-test job, with new tests in the lane bound by the ecosystem's test-determinism
fences (verbara-meta ADR-0004). Required-check status (informational vs. required) is a
grounding-time decision recorded per apply, consistent with the CI-gating standard
(verbara-meta ADR-0006 / ADR-0003).
## Requirements
### Requirement: A CI lane runs the live-DB Postgres test suites
The system SHALL run `Storage.Postgres.Tests` and `Identity.Redis.Tests` against real Postgres/Redis
containers in CI, as a lane distinct from the existing unit-test job that excludes them
(`ci.yml:83`, `:125`).

#### Scenario: Live-DB lane runs on every PR
- **GIVEN** a pull request that touches any Platform package
- **WHEN** CI runs
- **THEN** a dedicated job starts Postgres/Redis containers (Testcontainers, `pg_isready` wait
  strategy) and executes `Storage.Postgres.Tests` and `Identity.Redis.Tests`

### Requirement: Migration-dependent logic gets container-backed coverage
The system SHALL ensure that persistence logic which depends on real Postgres semantics (column
defaults, `ON CONFLICT`, migration-applied schema, jsonb operators) is exercised by at least one
live-DB test before it can ship, rather than relying solely on `Storage.InMemory` substitutes that
can silently diverge from the persisted behavior.

#### Scenario: A store method with Postgres-specific behavior has a live-DB test
- **GIVEN** a `Storage.Postgres` store method whose correctness depends on a Postgres-specific
  operator or a column populated only via migration (e.g. `due_date`, `payment_status`)
- **WHEN** the live-DB lane runs
- **THEN** a test in `Storage.Postgres.Tests` exercises that method against the real schema, not
  only the `Storage.InMemory` mirror

### Requirement: New container-backed tests in this lane follow the test-determinism fences
The system SHALL apply the ecosystem's test-determinism fences (verbara-meta ADR-0004) to any new
test added under this lane — no wall-clock sleeps driving container-dependent assertions, real-ms
intervals overridable via options, `FakeTimeProvider` for logical time.

#### Scenario: A new live-DB test asserts without a wall-clock sleep
- **GIVEN** a new test added to `Storage.Postgres.Tests` under this lane
- **WHEN** the test needs to wait on container-backed async state
- **THEN** it polls/awaits a real signal (e.g. `pg_isready`, a query result) rather than a fixed
  `Task.Delay`/`Thread.Sleep`

### Requirement: Required-check status is decided at grounding, not assumed here
The system SHALL leave the branch-protection required-check status of this lane (required vs.
informational) an open decision for grounding at apply time; this backlog change SHALL NOT presume
either answer.

#### Scenario: Apply-time grounding records the required-check decision
- **GIVEN** this change is picked up for implementation
- **WHEN** the apply-time design is finalized
- **THEN** the required-check status is recorded (required from day one, or informational-then-promoted)
  with its rationale, consistent with ADR-0003's CI-gating standard

