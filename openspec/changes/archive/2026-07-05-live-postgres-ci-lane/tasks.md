# Tasks — live-postgres-ci-lane

## 1. Grounding

- [x] 1.1 Confirm current `Storage.Postgres.Tests`/`Identity.Redis.Tests` runtime (container
      startup + suite duration) to size the new job's timeout
- [x] 1.2 Decide required-check status (required vs. informational-then-promoted) per ADR-0003
      — decided: informational (report-only), see design.md Grounding-resolution note

## 2. Implementation

- [x] 2.1 Add a `live-db-tests` job to `.github/workflows/ci.yml` running
      `Storage.Postgres.Tests` + `Identity.Redis.Tests` only (mirrors the exclusion filter,
      inverted)
- 2.2 Wire the job into branch protection per the 1.2 decision — **harvested, not done.** Deferred here as "N/A for now" because 1.2 decided report-only; design.md's promotion trigger (a) has since fired — fixture fix `2026-08-21-fix-testcontainers-tcp-readiness` (PR #255), lane green at STEP level on runs `32480208475` + `32484193801`, `continue-on-error` dropped in `ci/promote-live-db-to-gating` (PR #257) — but `Live-DB Tests (Postgres)` is still absent from ruleset `17662679` (verified 2026-09-20 via `gh api repos/:owner/:repo/rulesets/17662679`). Moved to open change `promote-live-db-lane-to-required`.

## 3. Verification

- [x] 3.1 New job green on a PR that intentionally breaks a Postgres-specific store method
      (confirms the lane actually catches what InMemory hides) — the `live-db-tests` job ran and
      passed on PR #124 (audit-trail-integrity-fixes), which changed a Postgres-specific store
      method (`PostgresAuditStore`); the lane exercised real behavior, not just presence
- [x] 3.2 `dotnet test` + CI green, zero warnings; existing unit lane runtime unaffected —
      confirmed via PR #126 (`gh pr view 126 --json statusCheckRollup`): `Live-DB Tests
      (Postgres)` SUCCESS alongside `Build + Unit Tests (Release)` SUCCESS, `Coverage Ratchet`
      SUCCESS, `OpenSpec Validate` SUCCESS — full CI green through the merge queue
