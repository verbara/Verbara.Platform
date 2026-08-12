## ADDED Requirements

### Requirement: Published images carry real provenance values

Every image published by `release.yml` MUST carry `org.opencontainers.image.revision`,
`org.opencontainers.image.created` and `org.opencontainers.image.version` with real values.

The root `Dockerfile` already declares the `ARG`s and the `LABEL` block, but the workflow's
`docker/build-push-action` step passes no `build-args`, so every CI-published image falls back to the
declared defaults and reports `unknown`. Only a local build with the variables exported gets real
values, which is the reverse of what matters: the images anyone else runs are the CI ones.

`revision` MUST identify the commit the image was built from, and `version` MUST match the release
tag the image is published under, so the two answers an operator needs — *what code is this* and
*what release is this* — are both readable from `docker inspect`.

#### Scenario: A released image names its commit

- **GIVEN** an image published by the release workflow
- **WHEN** its labels are inspected
- **THEN** `org.opencontainers.image.revision` is the commit the release was built from
- **AND** `org.opencontainers.image.version` matches the release tag

### Requirement: Every image in the release matrix is traceable

All four images built by the release matrix — `api`, `realtime`, `renderer` and `mail` — MUST carry
the provenance labels.

Only the root `Dockerfile` was given the `ARG`/`LABEL` block; `Dockerfile.realtime`,
`Dockerfile.renderer` and `Dockerfile.mail` carry no OCI labels at all, so passing build-args in the
workflow would leave three of the four images unchanged. A partially traceable matrix is the harder
state to reason about, because `docker inspect` returning nothing is indistinguishable from an image
built before the labels existed.

#### Scenario: The sibling images carry the same labels

- **GIVEN** the realtime, renderer and mail images from a release build
- **WHEN** their labels are inspected
- **THEN** each carries the same provenance labels as the api image, with the same values

### Requirement: Missing provenance fails the release

The release workflow MUST verify the labels on the image it just built and fail if
`org.opencontainers.image.revision` is absent or equal to `unknown`.

Without this, a renamed or dropped build-arg degrades silently to the current state — an image that
looks fine and answers nothing — and the regression is only discovered the next time someone
inspects an image, which is exactly the situation that motivated the labels.

The assertion MUST be verified against a real build before it is relied on as a gate. A check
written against a label the build does not actually emit would block legitimate releases, which is a
worse outcome than the gap it closes.

#### Scenario: An unstamped build does not publish

- **GIVEN** a release build where the provenance build-args are not passed
- **WHEN** the workflow reaches the verification step
- **THEN** the job fails
- **AND** the failure names the missing label

## Architectural Risk

**Level:** LOW

**Affected:**
- `.github/workflows/release.yml` — the build step and a new failing assertion in the release path.
- The three sibling Dockerfiles, which are otherwise untouched by this change.

**Mitigation:**
- Labels are inert metadata; nothing in the runtime reads them, so a wrong value cannot affect a
  running container. This is deliberately unrelated to `IMAGE_DIGEST`, which Pro's Layer C license
  check reads from the environment.
- The one way this change can cause harm is a verification step that blocks a good release, so the
  spec requires it to be proven against a real build rather than merged on the assumption it passes.
- Provenance labels are attacker-controllable on an untrusted image and are explicitly not offered
  as an integrity control; signing and digest pinning remain the mechanisms for that, and are out of
  scope here.
