---
tier: PEQUEÑO
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: Anyone shipping migration-dependent Postgres/Redis logic (billing, dunning, ledger, RBAC, sessions)
decision_ref: verbara-meta/ADR-0023
---

# Proposal: promote-live-db-lane-to-required

## Why

**This change exists to harvest one archived task that was deferred behind a named trigger, and
whose trigger has since fired.** It comes out of the `openspec validate --archived` sweep mandated
by verbara-meta/ADR-0023, which fixed the rule for an unticked box in an archived change: either
tick it with evidence, or move it into an open change — never tick without proof.

### The harvested box

`openspec/changes/archive/2026-07-05-live-postgres-ci-lane/tasks.md`, task **2.2**:

> Wire the job into branch protection per the 1.2 decision — N/A for now: 1.2 decided report-only,
> so there is no required-check entry to add yet; revisit at the promotion trigger documented in
> design.md

That is a deferral **with a trigger**, not a permanent N/A. Its own task 1.2 (ticked) records the
grounding decision as *"informational (report-only)"*, and the change's `design.md` names what would
reverse it:

> **Promotion trigger:** promote `live-db-tests` to a required check once EITHER (a) a fixture-level
> fix for the Postgres bootstrap-restart race lands (bounded retry or `Testcontainers.PostgreSql`
> adoption across the ~13 fixtures) and the lane then runs green for a few consecutive real PRs, or
> (b) the flake rate is independently re-measured as negligible on actual GitHub Actions runners

### Arm (a) is satisfied — verified at step level, not off a badge

1. **The fixture fix landed.** Archived change `openspec/changes/archive/2026-08-21-fix-testcontainers-tcp-readiness`
   (PR #255) applied a TCP-scoped `pg_isready -U postgres -h 127.0.0.1` probe across the fixtures.
2. **The lane then ran green on consecutive real PRs.** Read at **step** level, because while the
   job was `continue-on-error` its own conclusion was the only honest signal:
   - run `32480208475` — branch `fix/testcontainers-tcp-readiness` (the fix's own PR, #255):
     job `Live-DB Tests (Postgres)` `success`, steps *Live-DB tests — Storage.Postgres.Tests* and
     *— Identity.Redis.Tests* both `success`.
   - run `32484193801` — branch `fix/local-kind-datetimeoffset` (an **unrelated** PR, #256): same,
     both steps `success`.
3. **It has stayed green for a month, on the merge queue.** Runs `35542449427` (pr-298),
   `35524372439` (pr-274) and `35517549350` (pr-297): job `success`, both steps `success`.
4. **The lane was already promoted half-way.** PR #257, `ci: promote the Live-DB lane from
   report-only to gating`, dropped `continue-on-error` — a scan of the whole `live-db-tests` job
   block in `.github/workflows/ci.yml` now returns **0** occurrences of it, and the workflow header
   (`ci.yml:10-13`) reads *"gating since the readiness-probe fix graduated it, though still not a
   required check"*.

### But gating is not required, and nobody owns the difference

The promoting change said so in its own words, in the note that still sits above the job
(`ci.yml:284-287`):

> Gating is NOT the same as required: this check-run is still absent from the required-checks
> ruleset (17662679). Adding it there is a separate, deliberate repo-config change — a red job here
> is visible on the PR and blocks nothing automatically.

Confirmed live: `gh api repos/:owner/:repo/rulesets/17662679` returns exactly five required status
check contexts — `Build + Unit Tests (Release)`, `Coverage Ratchet`, `OpenSpec Validate`,
`AOT Publish (Api)`, `Invariant Gates`. `Live-DB Tests (Postgres)` is **not** among them.

And it is unowned: `grep -rln 'live-db-tests' openspec/changes/ --include='*.md'` outside
`archive/` returns nothing. The open change `promote-community-smoke-to-gating` cites ruleset
`17662679` only to prove that *its own* job is not a required context.

So the second half of a two-step promotion designed on 2026-07-05 is **outstanding, unblocked and
unowned**. Today a red Live-DB lane is advisory: it is visible on the PR and merges anyway — which
is precisely the gap the lane was built to close, since the suites it runs are the only ones that
exercise real Postgres semantics (column defaults, `ON CONFLICT`, migration-applied schema, jsonb
operators) that the `Storage.InMemory` mirror can silently diverge from.

## What Changes

- **Add `Live-DB Tests (Postgres)` to the required status checks of the `main-protection` ruleset
  (`17662679`).** This is a **repository-configuration** action (`gh api`), not a file edit — the
  ruleset is not stored in this repo. The context name is the job's `name:` value
  (`ci.yml:289`), which must be matched verbatim.
- **Rewrite the "gating is NOT the same as required" paragraph of the promotion note**
  (`ci.yml:284-287`) and the job-level comment (`ci.yml:290-292`), both of which assert a posture
  that this change makes false. The note becomes the record of the completed two-step promotion and
  cites the archived change and task this discharges.
- **Record the three operational consequences, each verified before proposing** (detail in
  `design.md`):
  1. **The docs-only fast path stays safe.** `live-db-tests` carries `needs: gate` and the
     byte-identical `if:` guard used by `build-and-test`, `coverage` and `aot-probe` — three jobs
     that are *already* required contexts. A required job that skips under that guard satisfies
     protection (verbara-meta/ADR-0016 §1, field-proven for this repo's ruleset in §4).
  2. **Dependabot PRs stay mergeable.** The CI-load skip on this job is **step**-level
     (`ci.yml:333-335`, `:340-341`), so the check-run *name* still reports on a bot PR. A
     job-level skip would collapse the context; it is not used here and must not be introduced.
  3. **A transient red now costs a re-run.** On PR #257 the first attempt of run `32485793699`
     had this job conclude `failure` at its *Build (Release, warnings-as-errors)* step while every
     other job was green; attempt 2 was green with no code change. Under a required context that
     outcome blocks the PR until re-run. The remedy is a re-run — or, if it recurs, a retry on the
     restore/build step — never walking the promotion back.
- **`CHANGELOG.md` `[Unreleased]` entry.** No version bump.
- **No change to any job, step, trigger or timeout; nothing under `src/`, `tests/`, `docker/`,
  `Directory.Packages.props` or `Directory.Build.props`.** This change alters only *what a red
  Live-DB lane costs*, never what it runs or asserts.

## Capabilities

### New Capabilities

<!-- None. The lane and its CI posture both belong to the existing `live-db-ci-lane` living
     capability (openspec/specs/live-db-ci-lane/spec.md). -->

### Modified Capabilities

- `live-db-ci-lane`: its fourth requirement — *"Required-check status is decided at grounding, not
  assumed here"* — is a placeholder that deliberately declined to answer the question. This change
  **discharges** it: the requirement is MODIFIED into the recorded decision (required, reached by
  the two-step evidence-gated route its own promotion trigger specified), and two requirements are
  ADDED — that the lane is a required context, and that the fast-path and bot-PR skips must keep
  the context reporting rather than collapsing it.

## Impact

- **Repository configuration:** ruleset `17662679` gains one required status check context,
  `Live-DB Tests (Postgres)` (5 → 6). Not a tracked file; applied with `gh api` and verified by
  re-reading the ruleset.
- **CI:** `.github/workflows/ci.yml` — **comments only** (the promotion note at `:262-288` and the
  job-level note at `:290-292`). No job, step, `needs:`, `if:`, trigger or timeout changes.
- **Docs:** `CHANGELOG.md` `[Unreleased]` gains a `### Changed — CI` entry.
- **Versioning:** no `Directory.Build.props` bump. CI-only changes ride the next release train as
  `[Unreleased]` entries.
- **Cross-repo:** none. No Sdk/Pro edit, no pack, no cache clear, no restore cascade, no pin move
  along `Sdk → Sdk.Pro → Platform ← Platform.Web`.
- **Timing:** ruleset changes take effect immediately and repo-wide, including on this change's own
  PR. Sequencing is therefore load-bearing and is fixed in `tasks.md` (Phase B applies the ruleset
  *after* the branch is green, so this PR does not become the first casualty of its own gate).

### Out of Scope (explicit)

- **The post-release smoke job in `.github/workflows/release.yml`.** A different workflow, a
  different job, and already owned by the open change `promote-community-smoke-to-gating` (PR #221),
  which harvests archived task 3.2 of
  `openspec/changes/archive/2026-07-26-license-gated-engine-health-degraded`. Nothing here touches
  `release.yml`.
- **The other non-required CI contexts** — `Coverage Script Tests`, `Analyze (C#)`,
  `Dependency Review`, `CodeQL`. Each is a separate grounding decision with its own evidence bar;
  none is promoted here.
- **The docs-only fast path itself** — `scripts/ci/classify-docs-only.sh`, the `gate` job, its
  allowlist and every other job's `needs: gate`/`if:` guard are consumed exactly as they are.
- **Any other ruleset property** — bypass actors, PR review counts, linear history, the merge-queue
  configuration. Only the required-status-checks list is edited, and only by appending.
- **No ADR is authored here.** The decision this records is the one the archived change's own
  promotion trigger already specified; it reuses `decision_ref: verbara-meta/ADR-0023` as the
  harvest's provenance and verbara-meta/ADR-0003 as the governing CI-gating standard.
- **Recorded, not fixed:** `ci.yml:364` cites an `ADR-0038` contract for preserving the
  `Coverage Ratchet` check name, but `docs/decisions/` contains no ADR-0038 (the highest is
  `0037-canonical-rbac-permission-vocabulary.md`). A dangling citation found while reading the
  file; left alone here because it concerns a different job and would widen this change's diff.
