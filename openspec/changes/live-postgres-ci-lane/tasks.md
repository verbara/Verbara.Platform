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
- [ ] 2.2 Wire the job into branch protection per the 1.2 decision — N/A for now: 1.2 decided
      report-only, so there is no required-check entry to add yet; revisit at the promotion
      trigger documented in design.md

## 3. Verification

- [ ] 3.1 New job green on a PR that intentionally breaks a Postgres-specific store method
      (confirms the lane actually catches what InMemory hides) — needs a real PR run, not
      verifiable from a local worktree
- [ ] 3.2 `dotnet test` + CI green, zero warnings; existing unit lane runtime unaffected —
      zero-warnings build and unaffected `build-and-test` filter confirmed locally; actual CI
      green needs a real PR/merge_group run
