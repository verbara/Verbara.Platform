---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Anyone shipping migration-dependent Postgres logic (billing, dunning, ledger, RBAC)
decision_ref: verbara-meta/ADR-0006
---

# Proposal: live-postgres-ci-lane

## Why

`.github/workflows/ci.yml` excludes the two container-backed test projects from every run
(`--filter "FullyQualifiedName!~Storage.Postgres.Tests&FullyQualifiedName!~Identity.Redis.Tests"`,
`ci.yml:83` and `:125`) — a deliberate, documented tradeoff (`ci.yml:69-80`), not an oversight. But
the cost of that tradeoff is no longer hypothetical: the overage→dunning pipeline (PR#88) and the
Typification E5 Art.17 redaction path both shipped with real bugs — Postgres never persisted
`due_date`/`payment_status`, `SaveAsync` was INSERT-only, `IssueInvoice` never set `DueDate`, and
Art.17 redaction was inert — that `InMemory`-only test runs hid, because CI has no live-DB lane
(`project_overage_dunning_shipped.md`, `project_typification_autonomous_disposition.md`). Both were
caught later, by hand, against a live Postgres. Testcontainers is already Platform's own fixture
pattern for this class of test (`tests/Verbara.Platform.Storage.Postgres.Tests/Seeds/PostgresRbacFixture.cs:16-30`,
`postgres:16-alpine` + `UntilCommandIsCompleted("pg_isready", ...)`), and the Sdk repo has a
dedicated ADR for the same substrate choice (`Verbara.Sdk/docs/decisions/0005-testcontainers-for-integration.md`).
The gap is CI wiring, not test authorship — `Storage.Postgres.Tests` already exists and already
uses Testcontainers; it simply never runs in CI.

## What Changes

Add a CI lane that runs `Storage.Postgres.Tests` (and any future migration-dependent suite) against
a real Postgres, so migration-order bugs and store-level persistence gaps are caught before merge
instead of after release.

## Capabilities

### New Capabilities

- `live-db-ci-lane`: a CI lane that exercises the live-DB Postgres test suites.

## Impact

`.github/workflows/ci.yml` (new job), no production code changes. Backlog: stays open until the
lane's exact trigger (blocking vs. informational, required-check status) is decided at grounding
time — see Architectural Risk.

## Architectural Risk

**Level:** MEDIUM — a new CI job that starts a real Postgres container is a new source of CI flake
(container startup time, port contention) if not built on the deterministic-fences discipline the
C1-C4 program already fought for (`project_deterministic_test_fences.md`). **Affected:** CI runtime
budget, branch-protection required-check set (ADR-0003), anyone whose PR touches
`Storage.Postgres`/`Storage.InMemory`/migrations. **Mitigation:** reuse the existing
`PostgresRbacFixture`-style Testcontainers wait strategy (`UntilCommandIsCompleted`, never
`/proc/net/tcp` polling — Sdk ADR-0005's explicit warning); defer the required-vs-informational
required-check decision to grounding rather than pre-deciding it here (see spec requirement).
