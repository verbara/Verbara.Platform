# released-image-smoke — Delta

## ADDED Requirements

### Requirement: Post-release smoke runs the demo compose stack from released digests
The system SHALL bring up `docker/demo/docker-compose.demo.yml` pinned to the digests of the
just-released images (not floating tags) as the post-release smoke substrate.

#### Scenario: Smoke stack starts from released digests
- **GIVEN** a Platform release has just been tagged and its images pushed + cosign-signed
- **WHEN** the smoke step runs
- **THEN** the demo compose stack is started with each service image pinned to that release's
  verified digest

### Requirement: One end-to-end journey is green after every release
The system SHALL exercise at least one complete end-to-end user journey against the smoke stack
and treat the release as unverified functionally if that journey fails, following walking-skeleton
scope (one journey, not full scenario coverage, per the initial cut).

#### Scenario: Core journey passes against a healthy release
- **GIVEN** the smoke stack is up and all services report healthy
- **WHEN** the smoke check runs the one designated end-to-end journey
- **THEN** the journey completes successfully and the release is marked functionally smoke-tested

#### Scenario: A broken released image fails the smoke check
- **GIVEN** a released image that boots but cannot complete the designated journey (e.g. a
  misconfigured connection string baked into the wrong image)
- **WHEN** the smoke check runs
- **THEN** the journey fails and the release is flagged, distinct from the cosign signature check
  which would still pass

### Requirement: Readiness is binary, not wall-clock
The system SHALL determine service readiness via binary health signals (e.g. `/health/ready` or the
per-service health check already defined in `docker-compose.demo.yml`) and SHALL NOT gate the
journey behind a fixed sleep/wall-clock wait.

#### Scenario: Smoke check waits on health, not a timer
- **GIVEN** the smoke stack is starting up
- **WHEN** the smoke runner determines whether to proceed
- **THEN** it polls each service's health endpoint until healthy (or a bounded retry/backoff limit
  is reached) rather than sleeping a fixed duration before assuming readiness

### Requirement: Repo boundary — this change starts and stays inside Platform
The system SHALL implement this smoke check inside the Platform repo. Graduating it into a shared
cross-repo E2E harness repo, if ever warranted, SHALL be a separate, explicitly decided change and
is not built or scaffolded by this one.

#### Scenario: No new repo is created by this change
- **GIVEN** this change is implemented
- **WHEN** reviewing what repos were touched
- **THEN** only Platform's own tree changed (workflow + demo-compose wiring); no new repo exists
