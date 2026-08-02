## Context

`.github/workflows/release.yml` fires on `push: tags: ['v*']` (`:32-35`) and has **exactly three
jobs** —
`python3 -c "import yaml; print(sorted(yaml.safe_load(open('.github/workflows/release.yml'))['jobs']))"`
→ `['github-release', 'release', 'smoke']`, declared at `:46`, `:354` and `:471`:

```
push v* tag
  └─ release            (:46)   matrix ×4 (api / realtime / renderer / mail), fail-fast: false
        :74   retroactive-tag guard  -> steps.retro.outputs.skip, exported as outputs.retro_skip (:49-53)
        :128  Build and push final image          <- IRREVERSIBLE (public ghcr.io push)
        :160  Sign final image with cosign        <- IRREVERSIBLE (signature published)
        :205  Verify cosign signature
        :239  Upload this leg's digest            (continue-on-error :241)
        :255  Authorized-digests reminder         (continue-on-error :257)
        ├─ github-release (:354)  needs: release · if: result=='success' && retro_skip!='true' (:362)
        │     :371 Collect the image digests      (continue-on-error :377)
        │     :384 Create GitHub Release          (idempotent: `gh release view` -> exit 0, :395-398)
        └─ smoke          (:471)  needs: release · if: always() && result=='success' (:474)
              continue-on-error: true (:476)
              :491 Run released-image smoke journey
                   ./docker/verbara-smoke-released.sh --tag "${GITHUB_REF_NAME}"   (:494)
```

`github-release` and `smoke` are **siblings**, not a chain: both `needs: release`, both start as
soon as all four matrix legs finish, and they run concurrently. By the time `smoke` starts, all
four images are already pushed **and** cosign-signed **and** signature-verified, and the
authorized-digests reminder (a *step inside* the matrix job, `:255`) has already printed to both
the log and the run's Step Summary.

`docker/verbara-smoke-released.sh` (344 lines) resolves the tag's digest, cosign-verifies it,
pins `docker/demo/docker-compose.demo.yml` to it via
`docker/smoke/docker-compose.smoke-override.yml`, polls `/health/ready` binarily (`:207-237`,
`exit 3` at `:235`), asserts the
community-boot readiness contract (`:239-290`), then runs the setup→login Postgres journey. Its
exit-code contract is documented at `:45-49`: `0` pass, `1` journey failed, `2`
environment/setup error, `3` readiness timed out (diagnostic-only outer bound).

### The evidence that the pre-condition is met

`gh run list --workflow=release.yml` + `gh run view <id> --json jobs` over every Release run that
has executed the `smoke` job:

| Run | Tag | `smoke` job | Job id | Log verdict |
|---|---|---|---|---|
| 28765393472 | `v2.17.0` | success | 85289188437 | `=== SMOKE PASSED: released image boots, serves traffic, and round-trips Postgres ===` |
| 29186371733 | `v2.18.0` | success | 86633603090 | same `SMOKE PASSED` banner |
| 29349709838 | `v2.19.0` | success | 87143828153 | same `SMOKE PASSED` banner |
| 29743829449 | `v2.20.0` | **failure** | 88359410736 | `=== SMOKE FAILED: platform-api never reported /health/ready within the 180s diagnostic bound ===` + `Health check dialer-engine with status Unhealthy completed after 0.0012ms with message 'dialer license blocked: Revoked'` + `Request finished HTTP/1.1 GET http://localhost:5000/health/ready - 503` |
| 30095986498 | `v2.21.0` | **failure** | 89491612862 | same signature (banner + `dialer license blocked: Revoked` + the 503 stream) |
| 30184375138 | `v2.21.1` | **failure** | 89746794513 | same signature |
| 30228537971 | `v2.21.2` | success | 89863734271 | `Community-boot readiness contract OK` + the `SMOKE PASSED` banner |
| 30324647348 | `v2.22.0` | success | 90168452552 | `Community-boot readiness contract OK` + the `SMOKE PASSED` banner |

That is the complete set: the job landed with `065ce2f0` (#127, 2026-07-05) and `v2.17.0` was the
first tag after it; `v2.16.0`'s run (`28339982389`) has only the four matrix legs.

Two facts fall out of that table and they point the same way:

1. **The two-consecutive-green pre-condition is met** — `v2.21.2` then `v2.22.0` are the only two
   Release runs since the fix, and nothing has been tagged since 2026-07-28. Both ran the
   sharpened script — `git show <tag>:docker/verbara-smoke-released.sh | grep -cE 'dialer license
   blocked|Degraded'` returns `10` for `v2.21.2`, `10` for `v2.22.0`, `10` at `HEAD`, and `0` for
   `v2.21.1`.
2. **The track record is not "unproven", it is 3 true positives and 0 flakes in 8 runs.** The
   three reds are the same defect (`dialer license blocked: Revoked` → permanent
   503 → readiness timeout, exit 3) — the very defect Verbara.Sdk.Pro/ADR-0017 fixed. All eight
   *workflow* runs concluded `success`. Report-only did not bound the blast radius of a flaky
   stage; it suppressed a correct alarm three releases running.

Note the sharpened `dialer-engine`-`Degraded` assertion added by #194 was **not** what caught
those three: the pre-existing binary `/health/ready` readiness poll did (`v2.21.1`'s script has 0
matches for the sharpened strings). The sharpening makes the assertion *specific*; the gating
makes any of it *audible*.

## Goals / Non-Goals

**Goals:**
- Discharge task 3.2 of `archive/2026-07-26-license-gated-engine-health-degraded` now that its
  task 6.4 pre-condition holds: remove `continue-on-error: true` from `release.yml:476` and stop
  the job name claiming report-only (`:472`).
- Answer, in the spec, the question `release.yml:326-335` left open — what gating *buys* on a
  job that runs after everything irreversible has happened — and write the requirement to match
  the honest answer rather than an aspirational one.
- Close the two defects gating exposes: the missing `retro_skip` guard (`:474` vs `:362`) and the
  orphaned/fused comment block (`:314-353`).
- Put the human stop-condition where the irreversible downstream act actually is (the
  authorized-digests runbook).

**Non-Goals:**
- Changing what the smoke asserts. `docker/verbara-smoke-released.sh` is untouched.
- Re-sequencing the job graph (D2).
- Removing any other `continue-on-error` in this workflow (`:241`, `:257`, `:377` — each named and
  excluded in the proposal's Out of Scope).
- Re-specifying the community-boot readiness contract itself — that is
  `community-boot-readiness`'s, pinned over Verbara.Sdk.Pro/ADR-0017 and untouched here.

## Decisions

**D1 — Gating = drop `continue-on-error`, and nothing more. The value is a durable red on the
tag; it is not the protection of any downstream step.**
This is the honest answer to `release.yml:326-335`, and the workflow's own comment already got it
right: *"there is no later step this failure could protect"* (`:331`). Verified against the job graph
above — the push (`:128`) and the cosign signature (`:160`) are complete before `smoke` starts,
so a red smoke cannot un-publish or un-sign anything, and GHCR deletion is a separate manual act
outside this workflow. What changes is the **workflow run conclusion for that tag**: today a
release with a broken community boot shows `success` (three times over, per the table); after
this change it shows `failure`, permanently, attached to the tag and the release commit, visible
in the Actions list, in the commit status and in `gh run list`. Given that the smoke's three reds
were all real and all silently ignored, a loud, durable, correct signal *is* the deliverable —
and the spec says exactly that instead of implying a protection that does not exist.
*Alternative considered:* leave it report-only and instead add an alerting/notification step.
*Rejected:* it invents a new notification surface for a signal GitHub already renders natively,
and it keeps `gh run list --workflow=release.yml` lying about release health.

**D2 — `github-release` is NOT made to `needs: smoke`.**
This is the only automatable downstream protection that exists — it would stop a functionally
broken release from being *announced* with generated notes, the four-row digest table and the
`Latest` badge (`release.yml:384-469`). It is rejected because it re-opens, three days after it
was closed, the exact gap that job was created to close. Commit `209311f3` (#207, 2026-07-28)
added `github-release` because *"`release.yml` built and cosign-signed the images but never
created the Release object, so every version reopened the gap — `v2.18.0` shipped tagged + signed
but release-less and was backfilled by hand on 2026-07-12 after an `/xr:pending` fact-check"*
(`release.yml:346-349`). Under `needs: smoke`, any smoke `exit 2` (environment/setup — e.g. a
ghcr.io resolution hiccup) or `exit 3` (readiness timeout) would leave a tag pushed, signed and
**release-less** again. Trading a loud red job for a silent missing Release object is a strictly
worse failure mode: the red is visible, the absence is not. The mitigation that would make this
palatable — re-running the job after the smoke is fixed — is exactly what a plain red smoke
already allows, without putting the Release object at risk.
*Also rejected for the same reason:* moving the *Authorized-digests reminder* (`:255`) out of the
matrix job into a smoke-gated job. It is informational (`:256`), it must never red a release
(`:256-257`), and its whole purpose is to be readable the moment the images exist.

**D3 — The human stop-condition goes in the authorized-digests runbook, not in the workflow.**
The genuinely irreversible post-release act is not in `release.yml` at all: it is the operator PR
that appends the `api` + `realtime` pair to verbara-website `data/authorized-digests.json`, whose
entries are read by the license issuer when minting `AuthorizedImageDigests` claims (Pro/ADR-0011
Layer C; runbook `docs/operations/2026-05-10-update-authorized-digests-after-release.md`, and the
drift sweep in `.github/workflows/digest-reconciliation.yml`). No job in Platform's `release.yml`
can gate a manual PR in another repo, so this change adds one stop-condition line under the
runbook's `## When to run this` (`:11-13`). The reminder step's own text is deliberately *not*
changed: it runs inside the `release` matrix job (`:255`), i.e. before a smoke result exists.

**D4 — Add `needs.release.outputs.retro_skip != 'true'` to the `smoke` job's `if`.**
`github-release` carries that conjunct (`:362`); `smoke` does not (`:474`). On a retroactive tag
(Platform/ADR-0024) the guard at `:74-102` skips every build/push/sign/verify step, `release` still
concludes `success`, `github-release` correctly skips — and `smoke` runs anyway, against an image
this run did not produce. Report-only made that invisible; gating makes it load-bearing, because
a retroactive tag's documented contract is that the workflow *"fires, sees the marker, exits
cleanly with a notice annotation, and ghcr.io is untouched"* (`:87-88`). Aligning the two `if`s
is the minimal fix and reuses the output the matrix job already exports (`:49-53`).
*Alternative:* leave it and accept the noise. *Rejected:* the first retroactive tag after this
merges would red a workflow whose entire point is to be a clean no-op.

**D5 — Say plainly that there is no separable "community leg".**
The follow-up text (`archive/2026-07-26-…/tasks.md:36-41`) and `community-boot-readiness`'s third
requirement both speak of graduating *"the community smoke leg"*. The workflow has no such leg:
there is one `smoke` job (`:471`) with one journey step (`:491-494`) invoking one script, and the
community-boot readiness assertion is a block *inside* that script (`docker/verbara-smoke-released.sh:239-290`).
Dropping `continue-on-error` therefore graduates the **whole** journey — digest resolution +
cosign verify + binary readiness poll + community-boot contract + setup→login Postgres round-trip
— and the spec delta says so rather than pretending a per-assertion promotion is possible. This
is not a scope expansion: those steps have run on every release since `v2.19.0` and are the ones
that produced all three true positives.

**D6 — Relocate and split the comment block at `release.yml:314-353`; do not merely edit it.**
It documents the `smoke` job but sits above `github-release:` (`:354`); the block ends at `:353`
and the job it describes starts 118 lines later at `:471`.
`git show 209311f3^:.github/workflows/release.yml` shows the block ending directly on
`smoke:` before #207; the diff hunk `@@ -314,6 +319,105 @@` inserted the new job and appended its
rationale to the same `#` run with no blank line, which is why `:346` now begins mid-sentence
(*"`release.yml` built and cosign-signed the images but never created the Release object…"*).
This change moves the smoke half back above `smoke:`, rewrites it for the gating posture (what
red means, what it does not stop, the retroactive-tag exemption, the runbook stop-condition), and
leaves the `github-release` half in place as its own separated block. The stale pointer
`openspec/changes/released-image-smoke` at `:314` becomes
`openspec/changes/archive/2026-07-05-released-image-smoke`.

**D7 — No version bump; a `[Unreleased]` CHANGELOG entry that rides the next train.**
Platform (unlike Verbara.Sdk.Pro, which tags on merge) cuts versions on an explicit
`chore(release): prep vX.Y.Z` commit that bumps `Directory.Build.props` `<PackageVersion>` and
rolls `[Unreleased]`; `git log -S'<PackageVersion>2.22.0</PackageVersion>' -- Directory.Build.props`
returns exactly one commit, `78415f93` (#203). CI-only changes have consistently ridden the next
train as `[Unreleased]` entries with no version of their own. The live precedent is the
`[Unreleased] → ### Changed — CI` block at `CHANGELOG.md:68-87` — the #207 `github-release` job,
same file, same posture (note it carries no PR citation; this change's entry should). Two earlier
`release.yml`/CI entries have since been rolled into `## [2.22.0]`: (#203) at
`CHANGELOG.md:186-200` under `### Changed` and (#200) at `CHANGELOG.md:171-184` under `### Added`.
The (#203) entry also fixes the *timing*
expectation for this change: *"Landed pre-tag on purpose: `release.yml` executes from the tagged
ref, so merging it after the tag would not affect this release."* The promotion binds the first
tag cut after merge; it cannot re-judge `v2.22.0`.

**D8 — Capability placement: `released-image-smoke`, not `community-boot-readiness`.**
`openspec/specs/released-image-smoke/spec.md` owns the smoke journey end to end (its four
requirements cover the digest-pinned substrate, the one journey, binary readiness and the repo
boundary) but says nothing about the job's CI posture — its second requirement's SHALL text stops
at *"treat the release as unverified functionally"* (`:25`) and its failure scenario at *"the
release is flagged"* (`:37`), an adjective with no owner. That is the gap this change fills, and it is
the right home because the posture applies to the whole journey (D5), not to one assertion.
`openspec/specs/community-boot-readiness/spec.md`'s third requirement already carries the
conditional *"Once this community smoke leg has run green **twice consecutively**, the leg SHALL
be graduated … to **gating** (drop `continue-on-error`, and/or make a later step `needs:` it)"*.
This change **discharges** that conditional and stays inside its `and/or` (it drops
`continue-on-error` and adds no `needs:`), so the requirement's text remains true verbatim and is
deliberately not restated. Modifying it would duplicate posture ownership across two capabilities.
Platform/ADR-0022 is not engaged: no source, no DTO, no serialization, no data access.

## Risks / Trade-offs

- **[A real-but-non-blocking smoke failure now reds a release whose images are already public]**
  → That is the intent, and it is what all three historical reds were. The red is advisory by
  construction: nothing in the repo consumes the Release workflow's conclusion (no `workflow_run`
  trigger exists in `.github/workflows/`, no README badge, and the `main-protection` ruleset
  `17662679` requires only `Build + Unit Tests (Release)`, `Coverage Ratchet`, `OpenSpec
  Validate`, `AOT Publish (Api)`, `Invariant Gates` — none of which is this job). Renaming the
  job is therefore safe: it is not a required status context.
- **[Infra flakiness turns a good release red]** → Measured flake rate is **0 in 8** smoke runs;
  every failure was the same true positive. Residual exposure is `exit 2` (digest resolution /
  ghcr.io) and `exit 3` (180s readiness bound) from the script's own contract (`:45-49`). Both
  are re-runnable in place (`Re-run failed jobs`), which under D2's graph costs nothing
  downstream because nothing depends on `smoke`. Green runs cost 26s (`v2.22.0`) to 40s
  (`v2.21.2`); a red costs the 180s bound plus teardown (~3m38s on `v2.21.1`).
- **[A retroactive tag reds the workflow]** → Removed by D4 before it can happen; the smoke job
  gets the same `retro_skip` conjunct `github-release` already has.
- **[The change cannot be validated by CI before it matters]** → `release.yml`'s `smoke` job runs
  only on a pushed `v*` tag, so no PR run exercises it (the same limitation the parent smoke
  change recorded at `archive/2026-07-05-released-image-smoke/tasks.md:22-25`), and
  `release-dryrun.yml` *"builds and publishes NOTHING"* (`:8`). Mitigation: the script is
  runnable locally against an already-published tag —
  `./docker/verbara-smoke-released.sh --tag v2.22.0` (`:28-37`) — which proves the assertion side
  green; the workflow side is a YAML edit whose only behavioural surface (`if`,
  `continue-on-error`, `name`) is verifiable by reading the rendered job in the next release run.
  The tasks below require exactly that post-merge confirmation rather than pretending PR CI
  covers it.
- **[Documentation drift]** → `release.yml:337-345` (the FOLLOW-UP paragraph) and the job name
  both assert report-only; both are rewritten in the same commit, so the workflow cannot claim a
  posture it no longer has.

## Migration Plan

1. (propose — this change) Author `.openspec.yaml`, proposal, design, tasks and the
   `released-image-smoke` delta. No workflow, source, test or doc edit.
2. (apply) Edit `.github/workflows/release.yml` only: rename the `smoke` job, add the
   `retro_skip` conjunct to its `if`, delete `continue-on-error: true`, and split/relocate/rewrite
   the comment block.
3. (apply) Add the runbook stop-condition line and the `CHANGELOG.md` `[Unreleased]` entry.
4. (merge) Standard PR; the heavy required jobs run because a `.github/**` diff is **not**
   docs-only under `scripts/ci/classify-docs-only.sh` — its allowlist is `docs/*`, `openspec/*`,
   `CHANGELOG.md` (`:12`), `*/README.md` (`:13`) and top-level `*.md` (`:17`), so
   `.github/workflows/release.yml` falls through to the nested-non-doc arm at `:16`.
5. (post-merge) On the next `v*` tag, confirm the rendered `smoke` job shows no
   `continue-on-error`, that its conclusion now propagates to the workflow run, and tick the
   verification task with the run id.

**Rollback:** restore `continue-on-error: true` at the `smoke` job and revert the job name — a
two-line revert of a single YAML file. The `retro_skip` conjunct (D4) and the comment relocation
(D6) are independent corrections and can stay. Because the smoke script is unchanged, rollback
restores the previous *reporting* posture exactly, with no behavioural residue.

## Open Questions

- None blocking. The one question the workflow comment left open — what gating buys when nothing
  downstream can be protected — is answered in D1 (a durable, correct red on the tag) and D2 (the
  one available downstream gate is rejected on evidence), and the spec delta is written to that
  answer rather than to a stronger claim.
