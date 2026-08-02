> **Execution model (Platform convention):** Subagent-Driven Development with FCM batching —
> **Phase A** foundation (batch in one subagent) → **Phase B** critical components (one focused
> subagent each) → **Phase C** integration (batch). Phases 1/2/3 below map to A/B/C; phase 4 is
> verification and phase 5 records what is deliberately not done.

## 1. Phase A — Foundation (batch): re-confirm the pre-condition at apply time

- [ ] 1.1 Re-derive the two-consecutive-green pre-condition immediately before editing the
  workflow, because a release cut between propose and apply would change the answer:
  `gh run list --workflow=release.yml --limit 5 --json databaseId,headBranch,conclusion,createdAt`
  then, for the two most recent runs,
  `gh run view <id> --json jobs -q '.jobs[]|select(.name|startswith("Post-release"))|"\(.name) \(.conclusion)"'`.
  The **job-level** conclusion is the only evidence — with `continue-on-error: true` the
  workflow-level conclusion is `success` regardless (run `30184375138` / `v2.21.1` is the proof:
  smoke job `failure`, workflow `success`). Expected at propose time: `30324647348` (`v2.22.0`)
  and `30228537971` (`v2.21.2`), both `success`.
- [ ] 1.2 Confirm both green runs are non-vacuous by checking the log verdict, not just the
  conclusion: `gh api repos/verbara/Verbara.Platform/actions/jobs/<job-id>/logs | grep -E
  "Community-boot readiness contract OK|SMOKE PASSED"` for jobs `89863734271` (`v2.21.2`) and
  `90168452552` (`v2.22.0`).
- [ ] 1.3 Confirm both ran the sharpened script:
  `for t in v2.21.2 v2.22.0; do git show $t:docker/verbara-smoke-released.sh | grep -cE 'dialer license blocked|Degraded'; done`
  → `10` and `10` (`HEAD` is also `10`; `v2.21.1` is `0`).
- [ ] 1.4 Re-read `.github/workflows/release.yml` and re-confirm the anchors this change depends
  on before editing: job list `:46` / `:354` / `:471`; smoke `name` `:472`, `needs` `:473`, `if`
  `:474`, `continue-on-error` `:476`, journey step `:491-494`; `github-release`'s `if` `:362`;
  the `retro_skip` output `:49-53` and the retroactive guard `:74-102`; the comment block
  `:314-353`.

**Phase A acceptance:** `gh run list --workflow=release.yml --limit 5 --json headBranch,conclusion`
plus the two `gh run view … --json jobs` calls in 1.1 show the two most recent Release runs with a
`success` smoke job, and 1.3's `grep -c` prints `10` for both tags. No file has been edited yet.

## 2. Phase B — Critical components (one focused subagent each)

### 2A. Gate the smoke job — `.github/workflows/release.yml`

- [ ] 2.1 Delete `continue-on-error: true` from the `smoke` job (`release.yml:476`). Do **not**
  touch the three other occurrences — `:241` *Upload this leg's digest*, `:257`
  *Authorized-digests reminder (api + realtime)*, `:377` *Collect the image digests* — each is
  best-effort by design and documented as such at `:228-230`, `:256` and `:373-375`.
- [ ] 2.2 Rename the job (`release.yml:472`) from `Post-release functional smoke (report-only)` to
  `Post-release functional smoke` so the name stops asserting a posture the job no longer has.
  Safe: the `main-protection` ruleset (`17662679`) requires only `Build + Unit Tests (Release)`,
  `Coverage Ratchet`, `OpenSpec Validate`, `AOT Publish (Api)` and `Invariant Gates`, and
  `release.yml` fires on tags, so this name is not a required status context.
- [ ] 2.3 Add the retroactive-tag guard to the smoke job's `if` (`release.yml:474`), matching
  `github-release`'s (`:362`): `if: always() && needs.release.result == 'success' &&
  needs.release.outputs.retro_skip != 'true'`. Reuses the output the matrix job already exports
  at `:49-53`; keep `always()` as-is (design D4, and out of scope per the proposal).
- [ ] 2.4 Do **not** add `needs: smoke` to `github-release` or to any other job, and do not move
  the *Authorized-digests reminder* step out of the `release` matrix job (design D2).

### 2B. Split and relocate the fused comment block — `.github/workflows/release.yml`

- [ ] 2.5 Move the smoke rationale (`release.yml:314-345`) from above `github-release:` (`:354`)
  to immediately above `smoke:` (`:471`), where it lived before commit `209311f3` (#207) inserted
  the new job between the comment and its job.
- [ ] 2.6 Leave the `github-release` rationale (`:346-353`) in place above `github-release:` as
  its own `#` block separated by a blank line, and restore a lead-in so `:346` no longer starts
  mid-sentence (`"`release.yml` built and cosign-signed the images but never created the Release
  object…"`).
- [ ] 2.7 Rewrite the relocated smoke comment for the gating posture: replace the `REPORT-ONLY
  (continue-on-error: true), not gating: …` paragraph (`:326-335`) and the `FOLLOW-UP
  (Verbara.Sdk.Pro/ADR-0017, tasks.md 3.2) … Deferred to a post-merge follow-up change.`
  paragraph (`:337-345`) with the resolved position: the job is gating; a red means "this
  released image set is functionally unverified"; it cannot un-publish or un-sign anything
  because push (`:128`) and cosign-sign (`:160`) already completed in the `release` job; nothing
  declares `needs: smoke` on purpose (cite the `v2.18.0` tagged-but-release-less incident the
  `github-release` job exists to prevent); and the operator stop condition lives in
  `docs/operations/2026-05-10-update-authorized-digests-after-release.md`. Cite
  `Verbara.Sdk.Pro/ADR-0017` and this change name.
- [ ] 2.8 Repoint the stale citation at `:314` from `openspec/changes/released-image-smoke` to
  `openspec/changes/archive/2026-07-05-released-image-smoke`.

### 2C. Operator stop condition — the authorized-digests runbook

- [ ] 2.9 In `docs/operations/2026-05-10-update-authorized-digests-after-release.md`, under
  `## When to run this` (`:11-13`, "After **every** Verbara.Platform tagged release."), add one
  stop-condition sentence: do **not** register the release's `api` + `realtime` digests while
  that tag's `Post-release functional smoke` job in the Release run is red — registering binds
  newly issued licenses' `AuthorizedImageDigests` claim to a functionally unverified image set
  (Pro/ADR-0011 Layer C). Do **not** edit the workflow's own reminder step text (`:255`): it runs
  inside the `release` matrix job, before any smoke result exists.

**Phase B acceptance:** `git diff --stat` lists exactly two files
(`.github/workflows/release.yml`, `docs/operations/2026-05-10-update-authorized-digests-after-release.md`);
`grep -cE '^\s+continue-on-error: true$' .github/workflows/release.yml` returns `3` (it is `4`
today — the string also appears in comments, so match the directive, not the word);
`grep -n 'retro_skip' .github/workflows/release.yml` shows it on both the `github-release` and the
`smoke` job conditions;
`python3 -c "import yaml; d=yaml.safe_load(open('.github/workflows/release.yml')); print(d['jobs']['smoke']['name'])"`
prints a name with no `report-only` substring (match the rendered `name:` value, not the word — a
rewritten comment may legitimately still say "was report-only until…", so `grep -i report-only`
over the whole file is NOT the check);
and `python3 -c "import yaml; print(sorted(yaml.safe_load(open('.github/workflows/release.yml'))['jobs']))"`
still prints `['github-release', 'release', 'smoke']`.

## 3. Phase C — Integration (batch)

- [ ] 3.1 Add the `CHANGELOG.md` `[Unreleased]` entry under the existing `### Changed — CI`
  heading (`CHANGELOG.md:68`): the post-release
  smoke job is now gating (`continue-on-error` dropped, job renamed), why it was safe to promote
  (three true positives and zero flakes in eight smoke runs, `v2.17.0` … `v2.22.0`; two
  consecutive greens on `v2.21.2` and
  `v2.22.0` against images carrying the Verbara.Sdk.Pro/ADR-0017 fix), what a red does and does
  **not** stop (no `needs: smoke`; images stay pushed and signed), the retroactive-tag guard, and
  the runbook stop condition. Cite `decision_ref: Verbara.Sdk.Pro/ADR-0017` and the PR number.
- [ ] 3.2 Do **not** bump `Directory.Build.props` `<PackageVersion>` (currently `2.22.0`).
  Platform cuts versions on an explicit `chore(release): prep vX.Y.Z` commit — `78415f93` (#203)
  is the only commit that has ever set `2.22.0` — and CI-only changes ride the next train as
  `[Unreleased]` entries. Live precedent: the `[Unreleased] → ### Changed — CI` block at
  `CHANGELOG.md:68-87` (the #207 `github-release` job, same file, no version of its own). Two
  earlier ones were rolled into `## [2.22.0]`: (#203) at `:186-200` under `### Changed`, (#200)
  at `:171-184` under `### Added`.
- [ ] 3.3 Record in the PR body that the promotion binds the **first tag cut after merge** and
  cannot re-judge `v2.22.0`, because `release.yml` executes from the tagged ref (the (#203)
  CHANGELOG entry states this: *"Landed pre-tag on purpose … merging it after the tag would not
  affect this release."*).

**Phase C acceptance:** `git diff --stat` lists exactly three files (the two from Phase B plus
`CHANGELOG.md`); `git diff -- Directory.Packages.props Directory.Build.props src tests docker`
is empty.

## 4. Verification

- [ ] 4.1 `dotnet build Verbara.Platform.slnx -c Release` completes with **zero warnings**
  (`TreatWarningsAsErrors=true`, `WarningLevel=9999`).
- [ ] 4.2 `dotnet test` green — no test is added or changed by this change, so this is a
  no-regression run over the existing suite.
- [ ] 4.3 `openspec validate --all --strict` green.
- [ ] 4.4 The edited workflow still parses and keeps its three jobs —
  `python3 -c "import yaml; d=yaml.safe_load(open('.github/workflows/release.yml')); print(sorted(d['jobs']), d['jobs']['smoke'].get('continue-on-error'), d['jobs']['smoke']['if'])"`
  prints `['github-release', 'release', 'smoke'] None` followed by the `if` expression from 2.3
  (`actionlint` is not installed on this machine; `yamllint` is available as a fallback).
- [ ] 4.5 Exercise the smoke script unchanged against an already-published tag to prove the
  assertion side is still green independent of the workflow edit:
  `./docker/verbara-smoke-released.sh --tag v2.22.0` exits `0` and logs
  `Community-boot readiness contract OK` followed by
  `=== SMOKE PASSED: released image boots, serves traffic, and round-trips Postgres ===`.
- [ ] 4.6 **CI green on the PR** — the full Platform required gate set runs, because a
  `.github/**` diff is NOT docs-only under `scripts/ci/classify-docs-only.sh` (allowlist:
  `docs/*`, `openspec/*`, `CHANGELOG.md` `:12`, `*/README.md` `:13`, top-level `*.md` `:17`; a
  `.github/**` path hits the nested-non-doc arm at `:16`): `Build + Unit Tests (Release)`, `Coverage Ratchet`,
  `OpenSpec Validate`, `AOT Publish (Api)`, `Invariant Gates` all green with zero warnings.
- [ ] 4.7 (post-merge, first `v*` tag after this lands) Confirm the promotion took effect on the
  real run: `gh run view <new-run-id> --json conclusion,jobs` shows a job named
  `Post-release functional smoke` (no "report-only"), and its conclusion now propagates to the
  workflow run's conclusion. Record the run id here. This cannot be verified from the PR — the
  smoke job only runs `needs: release` off a pushed release tag, and `release-dryrun.yml` builds
  and publishes nothing (`:8`); the parent smoke change recorded the same limitation at
  `openspec/changes/archive/2026-07-05-released-image-smoke/tasks.md:22-25`.

## 5. Out of scope (record, do not implement)

- [ ] 5.1 Confirm `docker/verbara-smoke-released.sh` is byte-identical to `HEAD` after the change
  (`git diff --exit-code -- docker/`): no assertion added, removed, sharpened or loosened.
- [ ] 5.2 Confirm the three other `continue-on-error: true` legs are untouched by name —
  `:241` *Upload this leg's digest*, `:257` *Authorized-digests reminder (api + realtime)*,
  `:377` *Collect the image digests*.
- [ ] 5.3 Confirm no `needs: smoke` edge exists anywhere in `release.yml` —
  `grep -nE '^\s+needs:' .github/workflows/release.yml` shows exactly two lines, both
  `needs: release` (the `github-release` job and the `smoke` job). Match the directive, not the
  word: `needs: smoke` appears in prose today at `:334` and may appear again in the rewritten
  comment.
- [ ] 5.4 Confirm `openspec/specs/community-boot-readiness/spec.md` is NOT edited: its gating
  clause is *discharged* by this change, not contradicted, and its text stays true verbatim.
- [ ] 5.5 Confirm no ADR is created — this change reuses `decision_ref:
  Verbara.Sdk.Pro/ADR-0017`, the same reference the parent change carried.
- [ ] 5.6 Confirm the licensed-profile smoke leg is still not built here (the separate follow-up
  declared at `archive/2026-07-26-license-gated-engine-health-degraded/proposal.md:77-78`).
- [ ] 5.7 Confirm `docs/roadmap.md:85` is left alone — its "sigue report-only" clause describes
  what `v2.21.2` shipped and remains historically accurate.
