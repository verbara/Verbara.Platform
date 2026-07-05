---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: Anyone consuming a tagged GHCR release (operators, the website's authorized-digests flow)
decision_ref: verbara-meta/ADR-0006
---

# Proposal: released-image-smoke

## Why

`docker/verbara-verify-image.sh` verifies a released image's cosign signature only — it confirms
provenance ("this image was signed by us"), never that the image actually works
(`docker/verbara-verify-image.sh:1-9`, "Verifies that an OCI image was signed... before the operator
runs `docker compose up`"). No step after tagging exercises a real user journey against the
released digests. `docker/demo/docker-compose.demo.yml` already assembles the 10-service substrate
(postgres, redis, asterisk, pstn-emulator, platform-api, realtime, web, nginx-gateway, prometheus,
grafana) and already has binary-readiness health checks wired for several services (e.g.
`docker-compose.demo.yml:145` `curl -sf http://localhost:5030/health || exit 1`) — it is the
substrate, not something new to build.

## What Changes

Add one post-release smoke check: bring up the demo compose stack pinned to the just-released
digests and run one end-to-end journey green, following walking-skeleton discipline (ONE scenario
first, same discipline as `tests/Verbara.Platform.E2E.Harness/README.md:17-19` — "ONE scenario:
`exactly-once`. ONE topology: `talos`.").

## Capabilities

### New Capabilities

- `released-image-smoke`: a post-release functional smoke check of cosign-verified released images.

## Impact

Adds a smoke step to the release flow (or a follow-up job it triggers); no production code
changes. Does not replace `verbara-verify-image.sh` (signature check stays); this is an additional,
later gate.

## Architectural Risk

**Level:** LOW — a smoke job that starts a known-good compose stack and asserts on one journey.
**Affected:** release-flow runtime (adds a stage after tag), nothing production-facing.
**Mitigation:** binary readiness only (`/health/ready`-style checks, no wall-clock fences — mirrors
the demo compose's existing health-check pattern); walking-skeleton scope (one journey) keeps the
blast radius of a flaky new stage small; this change starts and stays INSIDE Platform — a possible
future graduation into a shared cross-repo harness repo is a documented option, not something this
change builds.
