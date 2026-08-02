---
tier: PEQUEÑO
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: Platform operators (community / self-host deployments) + release engineering
decision_ref: Verbara.Sdk.Pro/ADR-0017
---

# Proposal: promote-community-smoke-to-gating

## Why

The post-release smoke job in `.github/workflows/release.yml` is **report-only**
(`continue-on-error: true`, `release.yml:476`), and that masking has already hidden a real,
customer-visible defect across **three consecutive releases**. Enumerating the `smoke` job's
conclusion on every Release run it has ever executed on
(`gh run list --workflow=release.yml` + `gh run view <id> --json jobs`):

| Release run | Tag | Started (UTC) | `smoke` job | Workflow run |
|---|---|---|---|---|
| 28765393472 | `v2.17.0` | 2026-07-06T03:14:37Z | success | success |
| 29186371733 | `v2.18.0` | 2026-07-12T08:46:14Z | success | success |
| 29349709838 | `v2.19.0` | 2026-07-14T16:27:20Z | success | success |
| 29743829449 | `v2.20.0` | 2026-07-20T12:51:42Z | **failure** | success |
| 30095986498 | `v2.21.0` | 2026-07-24T13:12:38Z | **failure** | success |
| 30184375138 | `v2.21.1` | 2026-07-26T02:19:32Z | **failure** | success |
| 30228537971 | `v2.21.2` | 2026-07-27T00:54:54Z | success | success |
| 30324647348 | `v2.22.0` | 2026-07-28T02:59:10Z | success | success |

That is **every** Release run the job has executed on: it landed in `release.yml` with commit
`065ce2f0` (#127, 2026-07-05) and first ran on `v2.17.0`; `v2.16.0`'s run
(`28339982389`) has only the four matrix legs.

All three reds are the **same true positive**, not flakes: each red job's failure banner is
`=== SMOKE FAILED: platform-api never reported /health/ready within the 180s diagnostic bound ===`,
printed over a container log line `Health check dialer-engine with status Unhealthy completed after
… with message 'dialer license blocked: Revoked'` (the elapsed-ms value varies per run) and a stream of
`Request finished HTTP/1.1 GET http://localhost:5000/health/ready - 503` — precisely
the permanently-un-ready community boot that Verbara.Sdk.Pro/ADR-0017 fixed and that Platform
consumed in `v2.21.2` (#194). **Every one of those eight workflow runs concluded `success`.** The
smoke did its job three times and nobody was told.

The graduation was designed, not improvised. `release.yml:326-335` already states the exit
criterion — *"REPORT-ONLY (continue-on-error: true), not gating: this is a new,
walking-skeleton-scope infra stage … there is no later step this failure could protect, so gating
would only turn a still-earning-trust stage into workflow-red noise on the release commit without
stopping anything. Promote to gating (drop continue-on-error, and/or make another step
`needs: smoke`) once it has a track record."* — and `release.yml:337-345` names the concrete
pre-condition under `FOLLOW-UP (Verbara.Sdk.Pro/ADR-0017, tasks.md 3.2)`: *"Graduate to gating
only AFTER the sharpened community leg has run green TWICE CONSECUTIVELY against images that
carry the fix (design D5)."* The open follow-up itself is
`openspec/changes/archive/2026-07-26-license-gated-engine-health-degraded/tasks.md:36-41`
(task 3.2, unticked on purpose) with its pre-condition at `:66-67` (task 6.4).

**The pre-condition is now satisfied.** The two most recent Release runs are the two above, and
both ran the sharpened script against images carrying the fix:

- run **30228537971** (`v2.21.2`), job `Post-release functional smoke (report-only)` id
  `89863734271`, 2026-07-27T01:01:43Z → 01:02:23Z, conclusion **success**; log
  `[01:02:17] Community-boot readiness contract OK (dialer-engine Degraded, 'dialer license
  blocked:' prefix present).` then `[01:02:18] === SMOKE PASSED: released image boots, serves
  traffic, and round-trips Postgres ===`.
- run **30324647348** (`v2.22.0`), job id `90168452552`, 2026-07-28T03:06:43Z → 03:07:09Z,
  conclusion **success**; log `[03:07:05] Community-boot readiness contract OK (…)` then
  `[03:07:06] === SMOKE PASSED: …`.

Both are non-vacuous: `git show v2.21.2:docker/verbara-smoke-released.sh` and
`git show v2.22.0:docker/verbara-smoke-released.sh` each match `dialer license blocked|Degraded`
**10** times (identical to `HEAD`), while `v2.21.1` matches **0** — the sharpening landed exactly
with `v2.21.2`. Because the job is `continue-on-error: true` the **workflow-level green proves
nothing**; the job-level conclusion plus the `SMOKE PASSED` log line are the only evidence, and
the `v2.21.1` row above is the counter-example that shows the job conclusion is reported
faithfully (`failure`) while the run stays `success`.

## What Changes

- **Gate the smoke job.** Drop `continue-on-error: true` from the `smoke` job
  (`release.yml:476`) so a failed post-release smoke turns the Release workflow run **red** for
  that tag, and rename the job from `Post-release functional smoke (report-only)`
  (`release.yml:472`) to a name that no longer claims report-only.
- **Do NOT re-sequence the job graph.** `github-release` (`release.yml:354`) is **not** made to
  `needs: smoke`. `release.yml` has exactly three jobs — `release` (`:46`, 4-image matrix),
  `github-release` (`:354`) and `smoke` (`:471`) — and the latter two are **siblings**, both
  `needs: release`, running in parallel. Everything irreversible (push `:128`, cosign-sign
  `:160`, verify `:205`, authorized-digests reminder `:255`) has already happened inside the
  `release` job before `smoke` starts. See design D2 for why holding the Release object was
  considered and rejected.
- **Add the retroactive-tag guard the smoke job is missing.** `github-release` skips retroactive
  tags via `needs.release.outputs.retro_skip != 'true'` (`release.yml:362`); `smoke` guards only
  on `always() && needs.release.result == 'success'` (`release.yml:474`) and therefore still runs
  on a tag whose run built, pushed and signed nothing (Platform/ADR-0024 marker handling,
  `release.yml:74-102`). Harmless while report-only; under gating it can red a tag whose
  documented contract is that the workflow *"fires, sees the marker, exits cleanly with a notice
  annotation, and ghcr.io is untouched"* (`release.yml:87-88`).
- **Fix the orphaned + fused comment block.** `release.yml:314-345` documents the `smoke` job but
  physically sits above `github-release:` at `:354`; the fused block ends at `:353` and the
  `smoke:` job it describes starts **118 lines later** at `:471` — commit `209311f3` (#207,
  2026-07-28) inserted the new job between the comment and its
  job (`git show 209311f3^:.github/workflows/release.yml` shows the block directly above `smoke:`
  before that commit), and appended the `github-release` rationale to the same block with no
  blank-line separator, leaving `:346` starting mid-sentence. The smoke half moves back above
  `smoke:` and is rewritten for the gating posture; the `github-release` half stays and gets its
  own separated block. The block's stale citation `openspec/changes/released-image-smoke`
  (`:314`) is repointed to the archived path.
- **Runbook stop-condition.** Add one line to `docs/operations/2026-05-10-update-authorized-digests-after-release.md`
  (§"When to run this", `:11-13`): do not register the release's digests in verbara-website while
  that tag's post-release smoke job is red. That operator PR — not anything in `release.yml` — is
  the irreversible downstream act, because it binds newly issued customer licenses to the digests
  (Pro/ADR-0011 Layer C).
- **CHANGELOG `[Unreleased]` entry, no version bump.** See Impact.
- **No change to `docker/verbara-smoke-released.sh`, to any `src/`/`tests/` file, or to any
  package pin.** This change only alters *what a smoke failure costs*, never what the smoke
  asserts.

## Capabilities

### New Capabilities

<!-- None. The smoke journey and its CI posture both belong to the existing `released-image-smoke`
     living capability (openspec/specs/released-image-smoke/spec.md); no new capability is
     warranted for a posture flip on a shipped one. -->

### Modified Capabilities

- `released-image-smoke`: the smoke journey's **CI posture** becomes part of the capability. The
  shipped requirement *"One end-to-end journey is green after every release"* is restated and
  extended so that "the release is flagged" is given a concrete, verifiable meaning (a red
  Release workflow run on that tag) instead of an unowned adjective, and three requirements are
  added: the job is gating rather than report-only; what a red smoke does and explicitly does
  **not** stop; and the retroactive-tag exemption.

<!-- `community-boot-readiness` (openspec/specs/community-boot-readiness/spec.md) is deliberately
     NOT modified. Its third requirement — "The released-image smoke asserts the `dialer-engine`
     Degraded shape as a gating leg" — already carries the conditional obligation "Once this
     community smoke leg has run green **twice consecutively**, the leg SHALL be graduated in
     `.github/workflows/release.yml` from report-only (`continue-on-error: true`) to **gating**
     (drop `continue-on-error`, and/or make a later step `needs:` it)". This change DISCHARGES
     that conditional; it does not contradict it, and the requirement's text stays true verbatim
     after the promotion. That capability owns WHAT the community boot must assert; this one owns
     WHAT A FAILURE COSTS. -->

## Impact

- **CI / release:** `.github/workflows/release.yml` only — the `smoke` job's `name` (`:472`),
  `if` (`:474`) and the removal of `continue-on-error: true` (`:476`), plus the relocation and
  rewrite of the comment block at `:314-353`. No other job, step or trigger changes.
- **Docs:** `docs/operations/2026-05-10-update-authorized-digests-after-release.md` gains one
  stop-condition line; `CHANGELOG.md` `[Unreleased]` gains a `### Changed — CI` entry.
- **Versioning: no `Directory.Build.props` bump.** Platform cuts versions on an explicit
  release-train commit (`chore(release): prep vX.Y.Z`); commit `78415f93` — the `v2.22.0` prep
  (#203) — is the only commit that has ever written `<PackageVersion>2.22.0</PackageVersion>`
  (`git log -S`). CI-only changes ride the next train as `[Unreleased]` entries and are rolled
  into the version section at release time. The live precedent is the current
  `[Unreleased] → ### Changed — CI` block (`CHANGELOG.md:68-87`), which is the #207
  `github-release` job — the same file, the same posture, no version of its own. The two
  `release.yml`/CI entries that already rode a train are now under `## [2.22.0]`: (#203)
  (authorized-digests reminder) at `CHANGELOG.md:186-200` under `### Changed`, and (#200)
  (docs/data-only CI fast-path) at `CHANGELOG.md:171-184` under `### Added`.
- **Timing:** the same (#203) precedent is load-bearing here — *"`release.yml` executes from the
  tagged ref, so merging it after the tag would not affect this release."* The promotion takes
  effect on the **first tag cut after this merges**; it cannot retroactively re-judge `v2.22.0`.
- **No production source, no tests, no pins.** Nothing under `src/`, `tests/`,
  `Directory.Packages.props` or `docker/` changes.
- **Cross-repo:** none in code. The consumed contract stays the Verbara.Sdk.Pro/ADR-0017 one
  already pinned by `community-boot-readiness`; no Pro or Sdk change, no pack/cache-clear/restore
  cascade. The only cross-repo *procedural* touch is the runbook line about the verbara-website
  `data/authorized-digests.json` PR, which stays a manual operator step in that repo.

### Out of Scope (explicit)

- **The three other `continue-on-error: true` occurrences in `release.yml`, by name and line —
  all stay untouched:** `:241` *Upload this leg's digest* (`actions/upload-artifact`, best-effort
  by design — `:228-230` "a hiccup here can never red a release whose images are already pushed
  and signed"); `:257` *Authorized-digests reminder (api + realtime)* (`:256` "Informational only
  — must never red a release whose image is already pushed+signed"); `:377` *Collect the image
  digests* (`actions/download-artifact` in `github-release`, `:373-375` "the release notes must
  still be created if the digests cannot be collected"). All three are inside the
  already-irreversible path and protect nothing by failing.
- **`docker/verbara-smoke-released.sh`.** No assertion is added, removed, sharpened or loosened;
  its exit-code contract (`:45-49`: 0 pass / 1 journey failed / 2 environment-setup error /
  3 readiness timeout) is consumed as-is.
- **Making `github-release` (or anything else) `needs: smoke`** — considered and rejected in
  design D2.
- **The licensed-profile smoke leg** — still the separate follow-up declared by the parent change
  (`archive/2026-07-26-license-gated-engine-health-degraded/proposal.md:77-78`), not built here.
- **`smoke`'s `always()` in `release.yml:474`** — left as-is (a no-op given the
  `needs.release.result == 'success'` conjunct); only the `retro_skip` conjunct is added.
- **`docs/roadmap.md:85`** — its `v2.21.2` row records that the leg "sigue report-only" as part of
  the historical description of what `v2.21.2` shipped, which was true then and stays true;
  historical roadmap rows are not rewritten.
