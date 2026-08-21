# timezone-independent-host Specification

## Purpose
Pin the Platform host's behaviour as **independent of the machine's local timezone**, so a
self-host operator anywhere in the world gets the same result as a UTC container.

This exists because a non-UTC host was not a cosmetic difference — it was an *install blocker*.
Under `Npgsql.EnableLegacyTimestampBehavior`, `timestamptz` reads came back with `Kind=Local`, and
`new DateTimeOffset(value, TimeSpan.Zero)` throws on a `Local`-kind value. That failed the
`QueueDistributionWorker` on every cycle and wedged `POST /api/v1/setup` half-completed, with the
retry then refused as `409 "Platform already initialized."`. Containers usually default to UTC,
which is why it went unreported; it fires on host-run binaries, dev machines, and any container
given a `TZ`.

The scope is the *mechanism*, not a general date-handling style guide: no projection, background
worker, or first-run setup path may depend on the process running in UTC, and values crossing an
untrusted ingress are **converted** to UTC (`ToUtcInstant()`), never relabelled — `SpecifyKind` is
the variant that silently shifts the instant.
## Requirements
### Requirement: The host behaves identically regardless of the machine's local timezone

Platform MUST NOT depend on the process running in UTC. Today it does:
`new DateTimeOffset(value, TimeSpan.Zero)` — the construction used in roughly a dozen Postgres row
projections — throws `ArgumentException: The UTC Offset of the local dateTime parameter does not
match the offset argument` whenever `value.Kind` is `DateTimeKind.Local`. On a host whose local
timezone is not UTC this is not a rare edge; it fired immediately on a UTC-5 machine running the
published binary against a real Postgres.

Every such construction MUST be made robust to a `Local`-kind input, and the chosen normalisation —
`DateTime.SpecifyKind(x, DateTimeKind.Utc)` where the column is known to hold UTC, or
`x.ToUniversalTime()` where the kind is genuinely unknown — MUST be applied **uniformly**. The two
are not equivalent: one relabels, the other converts. Choosing per site would silently shift some
timestamps and not others.

The change MUST also establish **why** the kind is `Local` at the source. Npgsql yields `Utc`-kind
values for `timestamptz`, so a `Local` kind implies a `timestamp without time zone` column, a
`DateTime.Now` upstream, or a type-mapping setting. If the projection is only the symptom, fixing it
alone leaves the same class of defect to resurface elsewhere.

#### Scenario: A store projection survives a non-UTC process timezone

- **GIVEN** the host process runs with a local timezone other than UTC
- **WHEN** a row carrying a timestamp is projected by any Postgres store
- **THEN** the projection completes without throwing
- **AND** the resulting instant is the same one the projection produces under `TZ=UTC`

#### Scenario: The background distribution loop does not fail on a non-UTC host

- **GIVEN** the host process runs with a local timezone other than UTC
- **WHEN** `QueueDistributionWorker` runs a distribution cycle
- **THEN** the cycle completes without logging `Distribution cycle failed`
- **AND** the failure does not recur every cycle for the process lifetime

### Requirement: A failed first-run setup leaves a retryable state

`POST /api/v1/setup` MUST NOT leave the deployment wedged when it fails part-way. Today it creates
the `platform` tenant, and if a later step throws, the tenant remains — so the next attempt hits the
"already initialized" guard and returns `409`, while no user was ever created. The operator's first
action on a fresh install ends in a state with no documented recovery short of deleting a row by
hand.

Setup MUST therefore either complete atomically, or base its already-initialized guard on evidence
that setup actually finished — the existence of a platform **user**, not merely the tenant. Either
choice satisfies this requirement; leaving a partial write that blocks retry does not.

#### Scenario: Setup can be retried after a mid-way failure

- **GIVEN** a fresh deployment where `POST /setup` failed after creating the platform tenant but
  before creating any user
- **WHEN** the operator retries `POST /setup` with valid input
- **THEN** the request succeeds and creates the platform user
- **AND** it does NOT return `409 "Platform already initialized."`

### Requirement: Regression coverage runs outside UTC

The suite MUST contain coverage that would have caught this. Every existing test passes today
precisely because CI runners and containers default to UTC, so timezone dependence is invisible to
the current suite by construction. At least one test MUST exercise the affected projections and the
setup path with the process timezone set to something other than UTC.

#### Scenario: A non-UTC test run fails if the dependence returns

- **GIVEN** the timezone regression coverage is present
- **WHEN** a change reintroduces a `new DateTimeOffset(x, TimeSpan.Zero)` over a `Local`-kind value
- **THEN** that test fails
- **AND** it fails on a UTC CI runner too, rather than only on a developer's machine

