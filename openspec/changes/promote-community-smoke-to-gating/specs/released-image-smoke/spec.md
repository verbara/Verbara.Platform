# released-image-smoke — Delta

Gives the smoke journey a **CI posture**. The capability already specifies what the post-release
smoke runs against (released digests), what it exercises (one end-to-end journey), and how it
waits (binary health signals) — but it never says what a failure costs: its SHALL text stops at
*"treat the release as unverified functionally"* and its failure scenario at the unowned adjective
*"the release is flagged"* (`openspec/specs/released-image-smoke/spec.md:25` and `:37`), neither of
which names an observable. In practice that meant
`continue-on-error: true` on the `smoke` job in `.github/workflows/release.yml:476` and three
consecutive Release runs (`v2.20.0`, `v2.21.0`, `v2.21.1`) concluding `success` while their smoke
job concluded `failure` on the same true positive.

This delta MODIFIES the "one end-to-end journey" requirement to define "flagged", and ADDS three
requirements: the job is gating, what a red smoke does and does not stop, and the retroactive-tag
exemption. It does not change `docker/verbara-smoke-released.sh`, any assertion it makes, or the
`community-boot-readiness` contract it asserts.

## ADDED Requirements

### Requirement: The post-release smoke job is gating, not report-only

The post-release smoke job in `.github/workflows/release.yml` MUST be **gating**: it SHALL NOT
carry `continue-on-error: true`, so a non-zero exit from `docker/verbara-smoke-released.sh` makes
the Release workflow run for that tag conclude **failure**. The job's `name` MUST NOT describe it
as report-only. This discharges the conditional obligation already stated by the
`community-boot-readiness` capability ("Once this community smoke leg has run green **twice
consecutively**, the leg SHALL be graduated … to **gating** (drop `continue-on-error`, and/or
make a later step `needs:` it)") by taking the first of its two options; no `needs:` edge is
added.

Gating SHALL apply to the smoke journey **as a whole**, not to one assertion within it. There is
no separable "community leg" in the workflow: one `smoke` job runs one step which invokes
`docker/verbara-smoke-released.sh --tag "${GITHUB_REF_NAME}"`, and the community-boot readiness
assertion is a block inside that script. Every exit path of the script — journey failure,
environment/setup error, and readiness timeout — therefore reds the run.

#### Scenario: A failing smoke reds the Release workflow run

- **GIVEN** a `v*` tag whose four images built, pushed and cosign-signed successfully
- **AND** the post-release smoke job has no `continue-on-error`
- **WHEN** `docker/verbara-smoke-released.sh` exits non-zero for any reason (journey failure,
  environment/setup error, or readiness timeout)
- **THEN** the smoke job concludes `failure`
- **AND** the Release workflow run for that tag concludes `failure`, not `success`

#### Scenario: A regression that was previously masked is now visible

- **GIVEN** a released image on which every unlicensed community boot answers `GET /health/ready`
  with 503 forever, so the smoke's binary readiness poll never succeeds
- **WHEN** the post-release smoke runs against that release
- **THEN** the smoke job fails **and** the Release workflow run fails
- **AND** the run is NOT reported as a successful release, which is what a report-only job did on
  the three consecutive releases that carried exactly this defect

#### Scenario: The job name does not contradict the posture

- **GIVEN** the smoke job is gating
- **WHEN** its `name` is read in the workflow file, in the Actions UI, or in `gh run view --json jobs`
- **THEN** that `name` value contains no `report-only` substring (case-insensitive)

### Requirement: What a red smoke means — and what it explicitly does not stop

A red post-release smoke SHALL be interpreted as **"this released image set is functionally
unverified"**, and the specification MUST NOT claim more than the job graph can deliver. Because
the smoke runs after the `release` job has already pushed and cosign-signed every image, a red
smoke SHALL NOT be expected to un-publish an image, revoke a signature, or suppress the
authorized-digests reminder. The deliverable of gating is a durable, correct failure conclusion
bound to the tag and the release commit.

The one irreversible downstream act that a red smoke MUST stop is **human**, not automated: the
operator PR that registers the release's `api` + `realtime` manifest digests in verbara-website's
`data/authorized-digests.json` (Pro/ADR-0011 Layer C image binding). Platform's release workflow
cannot gate a manual PR in another repository, so the stop condition SHALL be stated in
`docs/operations/2026-05-10-update-authorized-digests-after-release.md` as a pre-condition of
running that runbook.

#### Scenario: Images stay published when the smoke is red

- **GIVEN** a release whose images are already pushed and cosign-signed
- **WHEN** the post-release smoke fails and the workflow run concludes `failure`
- **THEN** the images and their signatures remain in ghcr.io untouched and still pass
  `docker/verbara-verify-image.sh`
- **AND** `gh run list --workflow=release.yml` reports that tag's run as `failure`, so the release
  is recorded as functionally unverified rather than silently successful

#### Scenario: The operator does not register digests for a functionally unverified release

- **GIVEN** a tag whose post-release smoke job concluded `failure`
- **WHEN** the operator opens
  `docs/operations/2026-05-10-update-authorized-digests-after-release.md` to add that release's
  `api` + `realtime` entries to verbara-website `data/authorized-digests.json`
- **THEN** the runbook states, before the capture step, that the digests MUST NOT be registered
  while that tag's smoke job is red — so newly issued licenses are not bound to a functionally
  unverified image set

#### Scenario: No downstream workflow step is made to depend on the smoke

- **GIVEN** the smoke job is gating
- **WHEN** the release workflow's job graph is inspected
- **THEN** no other job declares `needs:` on the smoke job — in particular the job that creates
  the GitHub Release object remains a sibling of the smoke job, so a smoke failure can never
  leave a tag pushed and signed but release-less

### Requirement: A retroactive tag is exempt from the gating smoke

The smoke job SHALL NOT run on a retroactive tag. Its condition MUST require the retroactive-tag
marker to be absent — the same guard the GitHub-Release job already applies via the `release`
job's exported `retro_skip` output — because a retroactive tag's run builds, pushes and signs
nothing and its documented contract is to exit cleanly with a notice annotation, leaving ghcr.io
untouched. Without the guard, a gating smoke would red a workflow whose entire purpose is to be a
clean no-op.

#### Scenario: A retroactive tag does not red the release workflow

- **GIVEN** an annotated tag carrying the retroactive-tag marker, so the `release` job skips every
  build, push, sign and verify step yet still concludes `success`
- **WHEN** the workflow evaluates the smoke job
- **THEN** the smoke job is skipped, exactly as the GitHub-Release job is
- **AND** the workflow run concludes cleanly with its notice annotation

#### Scenario: A normal tag still runs the gating smoke

- **GIVEN** a tag with no retroactive-tag marker whose four image legs all succeeded
- **WHEN** the workflow evaluates the smoke job
- **THEN** the smoke job runs and its result determines the workflow run's conclusion

## MODIFIED Requirements

### Requirement: One end-to-end journey is green after every release

The system SHALL exercise at least one complete end-to-end user journey against the smoke stack
and treat the release as unverified functionally if that journey fails, following walking-skeleton
scope (one journey, not full scenario coverage, per the initial cut). "Treating the release as
unverified" SHALL be a machine-observable outcome, not an informal note: the smoke job MUST fail
and the Release workflow run for that tag MUST conclude `failure`. A release whose journey failed
MUST NOT be reported as a successful Release workflow run.

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

#### Scenario: "Flagged" is a failed workflow run, not a passing one with a note

- **GIVEN** a released image whose designated journey fails
- **WHEN** the Release workflow run for that tag finishes
- **THEN** the run's conclusion is `failure`
- **AND** querying the run (for example `gh run list --workflow=release.yml`) reports that release
  as failed rather than successful

## Architectural Risk

- **Level:** LOW
- **Affected:** `.github/workflows/release.yml` only — the `smoke` job's `name`, its `if`
  condition, and the removal of `continue-on-error: true`, plus a relocated/rewritten comment
  block; and one stop-condition line in
  `docs/operations/2026-05-10-update-authorized-digests-after-release.md`. No production source,
  no tests, no package pins, and no change to `docker/verbara-smoke-released.sh` or to any
  assertion it makes. No cross-repo code impact along `Sdk → Sdk.Pro → Platform ← Platform.Web`:
  the consumed readiness contract stays the one `community-boot-readiness` already pins over
  Verbara.Sdk.Pro/ADR-0017, with no pack / cache-clear / restore cascade. The only cross-repo
  effect is procedural — the verbara-website `data/authorized-digests.json` PR remains a manual
  operator step, now with a documented stop condition.
- **Mitigation:** the promotion is evidence-gated, not scheduled. Across the eight Release runs on
  which the smoke job has executed (`v2.17.0` … `v2.22.0`) it produced three failures and zero
  flakes, and all three
  failures were the identical true positive (`dialer license blocked: Revoked` → `/health/ready`
  503 → readiness timeout) that Verbara.Sdk.Pro/ADR-0017 fixed; the two runs since that fix
  (`v2.21.2`, `v2.22.0`) were green with the sharpened assertion. The red is advisory by design:
  no `workflow_run` trigger, README badge, or `main-protection` required status context consumes
  the Release workflow's conclusion, and the job graph is left unchanged (no `needs: smoke`), so a
  smoke failure cannot block the GitHub Release object and re-open the "tagged + signed but
  release-less" gap. Residual flake exposure (digest-resolution errors, the 180-second readiness
  bound) is re-runnable in place at no downstream cost, and the retroactive-tag guard removes the
  one systematic false-red the gating would otherwise introduce. Rollback is restoring two lines
  in one YAML file.
