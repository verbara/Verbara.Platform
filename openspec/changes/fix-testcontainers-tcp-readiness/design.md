## Context

The `live-db-tests` job in `.github/workflows/ci.yml` carries `continue-on-error: true` on both of
its test steps, and its own comment root-causes why: `pg_isready` probes over the container's
internal Unix socket, while the host-side Npgsql client dials the published TCP port. The official
`postgres` entrypoint runs `initdb` against a temporary server with `listen_addresses=''`, so the
socket answers "accepting connections" seconds before anything is listening on TCP.

Measured against `postgres:16-alpine` (`socket` / `tcp` = exit codes, 0 = reachable):

```
t=17  socket=2  tcp=2
t=18  socket=0  tcp=2   <-- pg_isready over the socket says READY; TCP still refuses
t=19  socket=0  tcp=2
t=20  socket=2  tcp=2   <-- initdb's temp server shuts down
t=22  socket=0  tcp=0   <-- the real server is up
```

Testcontainers declares the container ready during that window, the fixture's first
`NpgsqlConnection.OpenAsync` hits a port that is not listening, and it dies with
`Exception while reading from stream` / connection reset — always inside `InitializeAsync`, never
mid-test.

**Severity is escalating, not stable.** Failures of `Storage.Postgres.Tests`:

| Where | Result |
|---|---|
| Local, clean `main` | 21 / 224 |
| CI, PR #212 | 115 / 237 |
| CI, PR #215 | **174 / 237** |

At 174/237 the project is 73% red and reports nothing usable about the DB-backed code it exists to
cover.

**The inventory is larger than the CI comment assumes.** That comment says "~13 Postgres fixtures".
The actual count:

- **18** fixtures call `pg_isready`.
- **17** still use the socket-scoped form; exactly one — `UserMfaEncryptionFixture`, shipped by
  `encrypt-mfa-secrets-at-rest` — already passes `-h 127.0.0.1`.
- **16** live in `Storage.Postgres.Tests`; **one** lives in
  `Channels.Sms.Tests/CsatSmsCorrelatorFixture.cs`.

**That last one matters more than its count suggests.** The fast lane excludes only
`Storage.Postgres.Tests` and `Identity.Redis.Tests`. Four projects use Testcontainers —
`Storage.Postgres.Tests`, `Identity.Redis.Tests`, `Channels.Sms.Tests`, `Mail.Tests` — so
**`Channels.Sms.Tests` starts a Postgres container inside the required `Build + Unit Tests` job**,
carrying the same racy probe. The premise that container-backed testing is confined to the
report-only lane is not true today.

Its exposure is genuinely lower — one container, not seventeen, and the race is worst under
concurrent starts — which is consistent with that job never having been observed failing on it. But
it is the same defect sitting in a **required** check, so it is fixed here rather than left to be
discovered under load.

`xunit.runner.json` with `parallelizeTestCollections: false` already exists in three projects
(`Storage.Postgres.Tests`, `Identity.Redis.Tests`, `Channels.Sms.Tests`) — evidence that container
contention was hit and worked around in each, without the underlying probe ever being corrected.

## Goals / Non-Goals

**Goals:**
- Make every Postgres fixture wait on TCP, in both projects, so a fixture never opens its first
  connection into a listener that is not up.
- Get `Storage.Postgres.Tests` to a stable, repeatable result so its output means something.
- Re-evaluate `parallelizeTestCollections: false` **by measurement**, and record the measurement
  whichever way it goes.
- Promote `Live-DB Tests (Postgres)` from report-only to gating — only after two consecutive green
  runs.
- Correct the `ci.yml` comment, which documents the unfixed state and the wrong fixture count.

**Non-Goals:**
- `Identity.Redis.Tests` — 34/34 green in CI and in every local configuration; the Redis image has
  no analogous bootstrap-then-restart cycle.
- `Mail.Tests` — uses Testcontainers but not a Postgres image, so the probe defect does not apply.
- Migrating to the `Testcontainers.PostgreSql` module (see D2).
- Any production source change. This is test infrastructure and CI configuration.

## Decisions

**D1 — Scope the probe to TCP with `-h 127.0.0.1`, rather than a bounded connect-retry.**
The CI comment names two candidate fixes: a bounded connect-retry in the fixture, or the
`Testcontainers.PostgreSql` module's log-based double-ready strategy. A third is cheaper and is
already proven in this repo: pass `-h 127.0.0.1` so `pg_isready` connects over TCP instead of the
Unix socket, which closes the exact window the comment describes. `UserMfaEncryptionFixture` has
used it since `encrypt-mfa-secrets-at-rest` and held **13/13 across repeated runs, including one CI
run in which 115 sibling tests failed around it**.
*Alternative — bounded connect-retry:* rejected as strictly worse: it masks the race with retries
instead of removing it, adds per-fixture code to 17 places, and leaves a wait strategy that still
lies about readiness. *Alternative — the module's log-based wait:* see D2.

**D2 — Do not adopt the `Testcontainers.PostgreSql` module in this change.**
It would also work and is arguably the more "correct" long-term shape. But it is a dependency change
rewriting the container construction in 17 fixtures, to solve a problem a two-argument probe fix
already solves and that is measured to work. Revisit only if `-h 127.0.0.1` proves insufficient under
CI load — which the evidence gate in D5 would surface.

**D3 — Fix all 17, not only the ones observed failing.**
Which fixtures fail varies run to run (21 → 115 → 174 across runs, different subsets each time),
so "the failing ones" is not a stable set. Every fixture that constructs a Postgres container gets
the corrected probe, including `Channels.Sms.Tests/CsatSmsCorrelatorFixture` in the required lane.
Each fixed fixture carries the same short comment `UserMfaEncryptionFixture` already has, so the next
fixture author copies the corrected shape rather than the broken one.

**D4 — Re-evaluate collection serialization by measurement, and only after the probe is fixed.**
`parallelizeTestCollections: false` was added to reduce concurrent-container pressure while the real
cause was a per-container timing window; it may now be buying only wall-clock cost. But the two
interact — parallel starts are exactly when the race was worst — so the measurement is only
meaningful *after* D1/D3 land. Run the suite repeatedly with and without it, record the numbers, and
remove it only if the parallel configuration is stable across repeated runs. **If the measurement
does not support removal, it stays and the reason is written down.** Do not remove it on reasoning.

**D5 — Promotion to gating is gated on two consecutive green runs, not on the code change.**
Same graduation discipline `released-image-smoke` uses. Promoting on the strength of the diff would
risk intermittently blocking unrelated PRs, which is precisely the outcome `continue-on-error: true`
was chosen to avoid. When it graduates, `continue-on-error` comes off **and** the job comment is
rewritten — it currently presents the race as open and states the wrong fixture count, so leaving it
would mislead the next reader into re-solving a solved problem.

**D6 — Correct the "container tests are confined to the report-only lane" premise explicitly.**
The `ci.yml` comment and the lane split both imply container-backed tests live outside
`build-and-test`. `Channels.Sms.Tests` and `Mail.Tests` falsify that. This change does **not**
relitigate the lane split — it fixes the probe everywhere and writes the true picture into the
comment, so the next person reasoning about CI structure starts from facts.

## Risks / Trade-offs

- **[A two-argument change across 17 files is easy to apply sloppily]** → The edit is mechanical and
  identical everywhere; a grep for the socket-scoped form must return zero afterwards, and that check
  is a task rather than an eyeball.
- **[`-h 127.0.0.1` could behave differently on the CI runner than locally]** → This is why D5 gates
  promotion on two green CI runs rather than on a local measurement. The failure mode of being wrong
  is a still-flaky report-only lane, not a blocked PR.
- **[Removing `parallelizeTestCollections: false` could trade one flake source for another]** →
  D4 forbids removing it on reasoning; it comes out only if repeated runs are stable, and the
  measurement is recorded either way.
- **[Touching `Channels.Sms.Tests` touches a required job]** → The change there is the same
  two-argument probe fix, and it strictly narrows the window in which the fixture can connect too
  early. It cannot make that job worse; if it did, the job is required and would say so immediately.
- **[The whole change is invisible to production]** → Accepted and intended. The value is that a
  73%-red project stops being noise, so the next real regression in DB-backed code is visible.

## Migration Plan

1. Land the probe fix across all 17 fixtures in one PR, with the corrected `ci.yml` comment.
2. Observe `live-db-tests` on that PR and on the next unrelated PR — two consecutive green runs.
3. In a follow-up PR, drop `continue-on-error` from both steps and note the two green runs that
   justified it.
4. Separately, measure and decide `parallelizeTestCollections` per D4; it may land in step 1 or in
   its own PR depending on what the numbers say.
5. No rollback plan is needed beyond reverting the commit: nothing outside tests and CI changes.

## Open Questions

- **Should `Channels.Sms.Tests` and `Mail.Tests` move into the live-db lane?** They are
  container-backed and currently sit in the fast required lane, which contradicts the lane split's
  stated intent. Moving them is a CI-structure decision with its own trade (the fast lane gets
  faster; those tests become report-only, which for a *required* check is a downgrade in signal).
  Out of scope here — this change fixes the probe wherever it lives — but it should be decided rather
  than left as an accident of history.
- **Is `postgres:16-alpine` still the right image for fixtures**, given the product ships against
  PostgreSQL 18 and the `docker-compose` stack runs `postgres:18-alpine`? Unrelated to the race, but
  the fixtures and production have drifted, and this change touches every one of those fixtures.
