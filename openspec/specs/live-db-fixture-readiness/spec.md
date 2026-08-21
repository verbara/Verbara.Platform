# live-db-fixture-readiness Specification

## Purpose
Pin how container-backed database test fixtures decide a container is **ready**, so the
`Live-DB Tests (Postgres)` lane stays trustworthy enough to gate on.

This exists because a readiness probe can green on a transport the test will never use. The
official Postgres entrypoint runs `initdb` against a *temporary* server started with
`listen_addresses=''`, so `pg_isready -U postgres` — which probes the container's **Unix socket** —
reports ready roughly 4s before anything is listening on TCP. Testcontainers then hands the fixture
a mapped port, docker's proxy accepts the connection and immediately closes it, and Npgsql surfaces
*"Attempted to read past the end of the stream"* inside `InitializeAsync()`. The result was a
*varying* subset of tests failing run to run (27 / 136 / 11 / 207 over four runs), which reads as
flakiness rather than as a fixable defect.

The scope is the readiness contract itself — probe the transport the test actually uses — plus the
discipline that follows from it: a lane graduates from report-only to gating on **evidence** (two
consecutive green runs, read at step level, because a `continue-on-error` job reports success even
when its steps fail), never on the strength of a diff.
## Requirements
### Requirement: Every Postgres fixture waits on TCP reachability, not the container's Unix socket

Every container-backed Postgres test fixture MUST gate its readiness on the transport the test
client actually dials. The fixtures use `pg_isready -U postgres`, which probes over the container's
**internal Unix socket**; the official `postgres` image runs `initdb` against a temporary server
with `listen_addresses=''`, so that probe reports "accepting connections" seconds before the
published TCP port is reachable. Testcontainers then declares the container ready, the host-side
`NpgsqlConnection.OpenAsync` dials a port that is not listening yet, and the fixture dies in
`InitializeAsync` with `Exception while reading from stream` / connection reset.

The probe MUST therefore be scoped to TCP — `pg_isready -U postgres -h 127.0.0.1` — or use an
equivalent strategy that observes the real listener. `UserMfaEncryptionFixture` already ships this
form and is the reference shape; the fix MUST be applied to every sibling fixture, not only the ones
observed failing, because which fixtures fail varies run to run.

Each fixed fixture MUST carry a comment naming the reason, so the next fixture author copies the
correct shape rather than the broken one.

#### Scenario: A fixture does not connect before the listener is up

- **GIVEN** a test fixture that starts a `postgres:*-alpine` container
- **WHEN** its wait strategy reports the container ready
- **THEN** the published TCP port is accepting connections
- **AND** the fixture's first `NpgsqlConnection.OpenAsync` succeeds without a connection reset

#### Scenario: The suite is stable across repeated runs

- **GIVEN** every Postgres fixture in the project uses the TCP-scoped probe
- **WHEN** the project's full test suite is run repeatedly
- **THEN** the pass/fail result is identical across runs
- **AND** no failure is an `NpgsqlException` originating in a fixture's `InitializeAsync`

### Requirement: The Live-DB lane is promoted to gating only on evidence

`Live-DB Tests (Postgres)` MUST NOT be promoted from report-only to a gating check until the fixture
fix has produced **two consecutive green runs**. The job currently carries `continue-on-error: true`
on both test steps, and its own comment states the promotion trigger is precisely a fixture-level
fix. Promoting on the strength of the code change alone — before the evidence exists — would risk
intermittently blocking unrelated PRs, which is the outcome the current posture was chosen to avoid.

Once promoted, `continue-on-error` MUST be removed so a red run actually blocks, and the job comment
MUST be updated: it currently documents the unfixed state and would otherwise mislead the next
reader into thinking the race is still open.

#### Scenario: Promotion waits for two green runs

- **GIVEN** the fixture fix has landed
- **WHEN** the `live-db-tests` job has run green twice consecutively
- **THEN** `continue-on-error` may be removed from its test steps
- **AND** until that point the job stays report-only

#### Scenario: The job comment matches reality after the fix

- **GIVEN** the fixture fix has landed
- **WHEN** the `live-db-tests` job comment in `ci.yml` is read
- **THEN** it describes the resolved state and the evidence that resolved it
- **AND** it no longer presents the socket-vs-TCP race as an open problem

### Requirement: Collection serialization is re-evaluated by measurement

The `parallelizeTestCollections: false` setting MUST be re-evaluated once the readiness probe is
correct, and the decision MUST be recorded with the measurement that drove it. That setting was
introduced to reduce concurrent-container pressure while the actual cause was a per-container timing
window, so it may now be buying only wall-clock cost. It MUST NOT be removed on reasoning alone —
if the measurement does not support removal, it stays and the reason is written down.

#### Scenario: The setting is kept or removed on evidence

- **GIVEN** the TCP-scoped probe is in place
- **WHEN** the suite is measured with and without collection parallelization
- **THEN** the setting is removed only if the parallel configuration is stable across repeated runs
- **AND** whichever way it goes, the measurement is recorded rather than the conclusion alone

