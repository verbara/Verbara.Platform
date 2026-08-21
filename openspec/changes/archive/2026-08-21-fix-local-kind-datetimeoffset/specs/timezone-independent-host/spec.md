## ADDED Requirements

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

## Architectural Risk

**Level:** MEDIUM

**Affected:**
- ~12 row projections across `src/Verbara.Platform.Storage.Postgres/Stores/` — these feed agent
  capacity, dunning, notifications, typification and AI suggestions, so a wrong normalisation shifts
  timestamps in user-visible data.
- `QueueDistributionWorker`, the product's core conversation-distribution loop.
- `SetupEndpoints`, the first thing an operator touches on a fresh install.
- **Cross-repo: none.** No SDK/Pro change, no pin movement, no Web contract change.

**Mitigation:**
- The distinction between relabelling (`SpecifyKind`) and converting (`ToUniversalTime`) is the
  whole risk, and the requirement forces one uniform choice rather than a per-site judgement —
  applying the wrong one uniformly is detectable, applying different ones per site is not.
- Establishing the upstream source of the `Local` kind is a requirement, not an optional extra, so
  the fix cannot degrade into symptom-patching that leaves the class alive.
- The regression coverage runs outside UTC, which is the specific control absent today.
- The setup recoverability half is independent of the timezone fix and stands on its own even if the
  root cause turns out to be narrower than expected.
