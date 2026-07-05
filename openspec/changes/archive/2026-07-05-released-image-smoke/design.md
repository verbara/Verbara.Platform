# Design — released-image-smoke

Backlog change: finalized at apply time.

- **Substrate:** `docker/demo/docker-compose.demo.yml`, re-pinned to the just-released digests
  (the same digests `verbara-verify-image.sh` verified and the website's authorized-digests flow
  records) rather than `latest`/`main`-floating tags.
- **Scope (walking skeleton):** ONE journey first — a login + one core action (e.g. inbound
  WebChat message → conversation created → agent sees it), not full scenario coverage. Mirrors
  `tests/Verbara.Platform.E2E.Harness`'s discipline of shipping exactly one scenario
  (`exactly-once`) before adding more.
  - **Do NOT** attempt to smoke every one of the 10 demo-compose services in the first cut — pick
    the smallest journey that proves the released images boot and talk to each other.
- **Readiness:** binary readiness checks only — poll `/health/ready` (or the equivalent per-service
  health endpoint already wired in `docker-compose.demo.yml`, e.g. line 145) until healthy or a
  bounded retry count is exhausted. **No wall-clock sleeps** gating the journey itself (ADR-0004
  test-determinism fences apply here too, even though this is infra not unit tests — the anti-flake
  principle is the same).
- **Trigger:** runs after a release tag lands (chained off `release.yml` or as a separate
  workflow_dispatch/scheduled follow-up) — exact wiring is an apply-time decision.
- **Repo boundary:** starts and stays INSIDE Platform. A shared cross-repo E2E harness repo is a
  documented *possible future* (the `xr` orchestration design's "graduation trigger"), not scope
  here — this change does not create or scaffold one.
- **References:** `docker/verbara-verify-image.sh` (signature-only precedent this extends),
  `docker/demo/docker-compose.demo.yml` (substrate), `tests/Verbara.Platform.E2E.Harness/README.md`
  (walking-skeleton precedent), verbara-meta ADR-0004 (determinism fences).
