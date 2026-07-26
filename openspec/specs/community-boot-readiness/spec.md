# community-boot-readiness Specification

## Purpose
TBD - created by archiving change license-gated-engine-health-degraded. Update Purpose after archive.
## Requirements
### Requirement: An unlicensed community boot is READY (HTTP 200) at `/health/ready`

Platform MUST return HTTP **200** from `GET /health/ready` on an unlicensed / community
(self-host) boot, so the pod joins the load balancer instead of being held permanently un-ready.
This is the consumer contract Platform pins over the Verbara.Sdk.Pro producer fix
(Verbara.Sdk.Pro/ADR-0017): a license-blocked Pro `dialer-engine` health check now settles
`Degraded` rather than `Unhealthy`, and because the `/health/ready` aggregate degrades (not fails)
on a `Degraded` member, the aggregate top-level `status` is `Degraded` — which the ASP.NET Core
health middleware maps to HTTP 200, not 503. Platform SHALL NOT add or change any health-check
source to achieve this; the aggregate flips 503 → 200 solely by bumping the `Verbara.Sdk.Pro.*`
pins to the version carrying the producer fix (`2.13.0-pro`).

The cross-repo readiness contract Platform consumes is frozen by the golden fixture
`Verbara.Sdk.Pro/openspec/changes/license-gated-engine-health-degraded/fixtures/health-ready-community-boot.json`,
emitted by `src/Verbara.Platform.Api/Health/HealthReportJsonWriter.cs`. The consumed field names —
cited verbatim, not paraphrased — are: the top-level `status`; the `checks` object; within it the
`dialer-engine` entry; that entry's `status` and `description`; and the stable `description` prefix
`dialer license blocked:`. The reason **suffix** (one of `NotLicensed`, `Revoked`, `Expired`,
`GraceExhausted`) is NOT part of the pinned contract: consumers MUST assert the `dialer license
blocked:` prefix and MUST NOT assert the exact suffix.

#### Scenario: `/health/ready` returns 200 on an unlicensed community boot

- **GIVEN** a Platform boot with a Postgres connection configured but no valid Pro license (a
  community / self-host deployment, where `AddProDialer` still registers the `ready`-tagged
  `dialer-engine` health check)
- **AND** the `Verbara.Sdk.Pro.*` pins are at the version carrying the producer fix (`2.13.0-pro`,
  Verbara.Sdk.Pro/ADR-0017), so the license-blocked `dialer-engine` check settles `Degraded`
- **WHEN** a client issues `GET /health/ready`
- **THEN** the response status code is **200** (NOT 503)
- **AND** the JSON body's top-level `status` is `Degraded`

#### Scenario: The `dialer-engine` check is `Degraded` with the `dialer license blocked:` prefix

- **GIVEN** the same unlicensed community boot serving `GET /health/ready` with HTTP 200
- **WHEN** the `/health/ready` JSON body (emitted by `HealthReportJsonWriter`) is parsed
- **THEN** the `checks` object contains a `dialer-engine` entry whose `status` is `Degraded`
- **AND** that entry's `description` STARTS WITH the stable prefix `dialer license blocked:`
- **AND** the assertion pins the `dialer license blocked:` prefix ONLY — it does NOT assert the
  reason suffix (`NotLicensed` / `Revoked` / `Expired` / `GraceExhausted`), which may vary

### Requirement: An integration test pins the community-boot readiness contract at the consumer

Platform MUST carry an integration test that asserts the community-boot readiness contract against
the actual `/health/ready` JSON body, so a producer or middleware regression that reverts the
`dialer-engine` entry to `Unhealthy` (flipping the aggregate back to 503) fails a Platform test
rather than silently degrading every community deployment. The test SHALL assert, over the JSON
emitted by `src/Verbara.Platform.Api/Health/HealthReportJsonWriter.cs`, that: the HTTP status is
`200`; the top-level `status` is `Degraded`; the `checks` object's `dialer-engine` entry has
`status` == `Degraded`; and that entry's `description` STARTS WITH the prefix `dialer license
blocked:`. The test MUST assert the prefix and MUST NOT assert the exact reason suffix.

#### Scenario: The readiness contract test fails if `dialer-engine` reverts to `Unhealthy`

- **GIVEN** the community-boot readiness integration test is present
- **WHEN** a regression makes the license-blocked `dialer-engine` check settle `Unhealthy` again
  (so `/health/ready` returns 503 and the top-level `status` is `Unhealthy`)
- **THEN** the test fails because the HTTP status is not `200`, the top-level `status` is not
  `Degraded`, and the `dialer-engine` entry's `status` is not `Degraded`

#### Scenario: The readiness contract test asserts the prefix, never the suffix

- **GIVEN** the community-boot readiness integration test is present
- **WHEN** the `dialer-engine` entry's `description` is `dialer license blocked: Revoked` on one
  boot and `dialer license blocked: NotLicensed` on another
- **THEN** the test passes in both cases because it asserts the `dialer license blocked:` prefix
  and does not pin the reason suffix

### Requirement: The released-image smoke asserts the `dialer-engine` Degraded shape as a gating leg

Platform's post-release smoke `docker/verbara-smoke-released.sh` MUST assert, on the community
(unlicensed) boot, not merely that `GET /health/ready` returns 200 but that the `/health/ready`
JSON body's `checks` object's `dialer-engine` entry has `status` == `Degraded` and a `description`
that STARTS WITH the prefix `dialer license blocked:`. The smoke MUST assert the prefix and MUST
NOT assert the exact reason suffix. Once this community smoke leg has run green **twice
consecutively**, the leg SHALL be graduated in `.github/workflows/release.yml` from report-only
(`continue-on-error: true`) to **gating** (drop `continue-on-error`, and/or make a later step
`needs:` it).

#### Scenario: Smoke fails a release whose community boot is not Degraded-with-reason

- **GIVEN** the sharpened `docker/verbara-smoke-released.sh` running against a released image on an
  unlicensed community boot
- **WHEN** `GET /health/ready` returns 200 but its `dialer-engine` entry is missing, or its
  `status` is not `Degraded`, or its `description` does not start with `dialer license blocked:`
- **THEN** the smoke check fails (a bare 200 is no longer sufficient to pass)

#### Scenario: The community smoke leg is gating after two consecutive green runs

- **GIVEN** the community smoke leg has run green twice consecutively while report-only
  (`continue-on-error: true`)
- **WHEN** it is graduated in `.github/workflows/release.yml`
- **THEN** `continue-on-error: true` is removed from that leg (and/or a later step `needs:` it) so a
  future community-boot readiness regression turns the release workflow red

