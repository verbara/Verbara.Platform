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
- [ ] 3.3 CI green, zero warnings
