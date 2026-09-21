# Tasks — released-image-smoke

## 1. Grounding

- [x] 1.1 Pick the ONE walking-skeleton journey (smallest path that proves released images boot
      and talk to each other end-to-end)
- [x] 1.2 Confirm which `docker-compose.demo.yml` services need digest-pinning vs. can stay
      floating (e.g. prometheus/grafana likely don't need pinning)

## 2. Implementation

- [x] 2.1 Smoke runner: re-pin demo compose to released digests, poll health endpoints (binary,
      no wall-clock), run the one journey
- [x] 2.2 Wire the smoke step to run after a release tag (chained off `release.yml` or a follow-up
      workflow — apply-time decision)

## 3. Verification

- [x] 3.1 Smoke check passes against a known-good release
- [x] 3.2 Smoke check fails against a deliberately broken image (proves it actually verifies
      function, not just signature)
- [x] 3.3 CI green, zero warnings — PR #127's own CI was green (merged 2026-07-05: `Build + Unit Tests (Release)` / `Analyze (C#)` / `Dependency Review` / `Coverage Ratchet` / `OpenSpec Validate` / `CodeQL` all SUCCESS). The deferred half reported on the next tagged release, `v2.17.0` (run `28765393472`, 2026-07-06), read at STEP level rather than off the `continue-on-error` badge: step `Run released-image smoke journey` = **success**; likewise `v2.18.0` (`29186371733`) and `v2.23.0` (`32946067672`). NOTE, so this tick is not misread: the same job later went red on `v2.20.0`/`v2.21.0`/`v2.21.1` — three true positives of a *product* defect (community `dialer license blocked: Revoked` un-ready boot), owned by open change `promote-community-smoke-to-gating`, not by this box.
