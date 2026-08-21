---
tier: PEQUEÑO
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: Anyone running `dotnet test` locally; the Live-DB CI lane
decision_ref: Platform/ADR-0003
---

## Why

`tests/Verbara.Platform.Storage.Postgres.Tests` fails a **varying** subset of its suite on every
run — 21/224 on a clean `main` locally, 142 → 49 → 8 across repeated local runs, and **122 passed /
115 failed of 237 in CI** (PR #212, run `30432527575`). Every failure is
`Npgsql.NpgsqlException: Exception while reading from stream` / connection-reset inside a fixture's
`InitializeAsync`; none is a logic failure.

`ci.yml`'s `live-db-tests` job already root-causes it and carries `continue-on-error: true` because
of it. That comment also names the promotion trigger verbatim:

> *"Promotion trigger: a fixture-level fix (bounded connect-retry, or adopting the
> Testcontainers.PostgreSql module's log-based double-ready wait strategy) across the ~13 Postgres
> fixtures, tracked as an OpenSpec follow-up once this lane ships."*

**This is that follow-up, and a third candidate fix is already proven in-repo.** The ~13 fixtures
wait on `pg_isready -U postgres`, which probes over the container's **internal Unix socket**. The
official `postgres` entrypoint runs `initdb` against a temp server with `listen_addresses=''`, so
that probe reports ready several seconds before anything is listening on TCP — which is what the
host-side Npgsql client actually dials. Measured against `postgres:16-alpine`:

```
t=17  socket=2  tcp=2
t=18  socket=0  tcp=2   <-- pg_isready over the socket says READY; TCP still refuses
t=19  socket=0  tcp=2
t=20  socket=2  tcp=2   <-- initdb's temp server shuts down
t=22  socket=0  tcp=0   <-- the real server is up
```

Adding `-h 127.0.0.1` forces the probe over TCP and closes the window. `UserMfaEncryptionFixture`
(shipped in `encrypt-mfa-secrets-at-rest`, #212) already does this and held **13/13 across repeated
runs, including one run in which 49 sibling tests failed around it**.

**Why now.** It is the single blocker on task 6.2 of the archived `encrypt-mfa-secrets-at-rest`
change, it makes a whole test project untrustworthy locally, and it is the stated precondition for
promoting `Live-DB Tests (Postgres)` from report-only to a gating check.

## What Changes

- **Back-port the TCP-scoped readiness probe** to every Postgres-backed Testcontainers fixture in
  `tests/Verbara.Platform.Storage.Postgres.Tests` — `UntilCommandIsCompleted("pg_isready", "-U",
  "postgres", "-h", "127.0.0.1")` — carrying the same explanatory comment
  `UserMfaEncryptionFixture` already has, so the next fixture author copies the fixed shape.
- **Re-evaluate `parallelizeTestCollections: false`.** The `xunit.runner.json` serialization was
  added to reduce concurrent-container pressure while the real cause was a per-container timing
  window. Once the probe is correct, measure whether serialization is still needed; if not, remove
  it and recover the wall-clock. Decide by measurement, not assumption — leave it in place if the
  numbers do not support removal.
- **Promote the lane only on evidence:** after the fixtures are fixed, `Live-DB Tests (Postgres)`
  graduates from `continue-on-error: true` to gating **only after two consecutive green runs**,
  mirroring the graduation discipline `released-image-smoke` already uses.
- **No production source change.** This is test infrastructure and CI configuration only.

## Capabilities

### New Capabilities
- `live-db-fixture-readiness`: every container-backed Postgres fixture waits on **TCP**
  reachability, not the container's internal Unix socket, so a fixture never opens its first
  connection into a listener that is not up yet.

### Modified Capabilities
<!-- None. `live-db-ci-lane` exists as a living spec but describes the lane's shape and its
     report-only posture; this change does not alter that spec's requirements — the promotion to
     gating is gated on evidence and is specified here as a new requirement rather than by
     rewriting the existing capability. -->

## Impact

- **Tests:** the ~13 fixture classes under `tests/Verbara.Platform.Storage.Postgres.Tests` that
  build a `postgres:*-alpine` container; possibly `xunit.runner.json` in that project and in
  `Verbara.Platform.Identity.Redis.Tests`.
- **CI:** `.github/workflows/ci.yml` — the `live-db-tests` job comment (which currently documents
  the unfixed state) and, after two green runs, its `continue-on-error` flags.
- **Unblocks:** task 6.2 of `encrypt-mfa-secrets-at-rest` (archived), which is explicitly left open
  pending this work.
- **No production source, no schema, no API, no cross-repo impact.**

### Out of Scope (explicit)

- **`Identity.Redis.Tests`** — 34/34 green in CI and in every local configuration; the Redis image
  has no analogous bootstrap-then-restart cycle. Touch it only if the `xunit.runner.json`
  re-evaluation covers it.
- **Migrating to the `Testcontainers.PostgreSql` module.** It is one of the two options the CI
  comment names and would also work, but it is a dependency change across ~13 fixtures for a
  problem a two-argument probe fix already solves. Reconsider only if `-h 127.0.0.1` proves
  insufficient under CI load.
