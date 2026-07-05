# Tasks — live-postgres-ci-lane

## 1. Grounding

- [ ] 1.1 Confirm current `Storage.Postgres.Tests`/`Identity.Redis.Tests` runtime (container
      startup + suite duration) to size the new job's timeout
- [ ] 1.2 Decide required-check status (required vs. informational-then-promoted) per ADR-0003

## 2. Implementation

- [ ] 2.1 Add a `live-db-tests` job to `.github/workflows/ci.yml` running
      `Storage.Postgres.Tests` + `Identity.Redis.Tests` only (mirrors the exclusion filter,
      inverted)
- [ ] 2.2 Wire the job into branch protection per the 1.2 decision

## 3. Verification

- [ ] 3.1 New job green on a PR that intentionally breaks a Postgres-specific store method
      (confirms the lane actually catches what InMemory hides)
- [ ] 3.2 `dotnet test` + CI green, zero warnings; existing unit lane runtime unaffected
