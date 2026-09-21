# Tasks — promote-live-db-lane-to-required

> **Execution model (Platform convention, `openspec/config.yaml` → `rules.tasks`):**
> Subagent-Driven Development with FCM batching — **Phase A** foundation (batch in one subagent) →
> **Phase B** critical components (one focused subagent each) → **Phase C** integration (batch).
> Phases 1/2/3 below map to A/B/C; phase 4 is verification and phase 5 records what is deliberately
> not done. A fresh subagent per task, never inline in the main session.
>
> **No test is written by this change.** It is not a bug fix, so the failing-regression-test-first
> rule does not apply; and it adds no capability to `src/`, so there is nothing for Phase C to batch
> tests around. The change's own assertions are CI-configuration checks, and they live in Phase 4.
>
> **No cross-repo sequence.** Nothing is edited in `Verbara.Sdk` or `Verbara.Sdk.Pro`, so there is
> no `dotnet pack` → clear NuGet cache → `dotnet restore` cascade to spell out.

## 1. Phase A — Foundation (batch): re-derive the evidence at apply time

The promotion is evidence-gated. Everything below was true when this change was proposed; a lane
that has gone red since would change the answer, so it is re-derived before anything is edited.

- [ ] 1.1 Re-confirm the lane is still **gating** — `continue-on-error` absent from the whole
  `live-db-tests` job block in `.github/workflows/ci.yml`. Scan the block, not the file: the string
  also appears in other jobs' comments. Expected: `0` occurrences inside the block.
- [ ] 1.2 Re-confirm the lane is still **not required** —
  `gh api repos/:owner/:repo/rulesets/17662679 --jq '.rules[]|select(.type=="required_status_checks")|.parameters.required_status_checks[].context'`
  Expected at propose time, exactly five: `Build + Unit Tests (Release)`, `Coverage Ratchet`,
  `OpenSpec Validate`, `AOT Publish (Api)`, `Invariant Gates`. If `Live-DB Tests (Postgres)` is
  already present, stop — the change is already applied.
- [ ] 1.3 Re-derive the green record at **step** level, never off a badge (while the lane was
  `continue-on-error` the workflow conclusion was `success` regardless):
  `gh run view <id> --json headBranch,conclusion,jobs --jq '.jobs[]|select(.name|test("Live-DB"))|{c:.conclusion,steps:[.steps[]|select(.name|test("Live-DB tests"))|.conclusion]}'`
  for the two promotion runs `32480208475` (`fix/testcontainers-tcp-readiness`, #255) and
  `32484193801` (`fix/local-kind-datetimeoffset`, #256 — an unrelated PR), and for the three
  merge-queue runs `35542449427` (pr-298), `35524372439` (pr-274) and `35517549350` (pr-297).
  Expected: job `success` with both steps `success` on all five.
- [ ] 1.4 Confirm the last few CI runs on `main`/merge-queue still show the lane green, so the
  promotion is not being applied over a fresh regression:
  `gh run list --workflow=ci.yml --limit 10 --json databaseId,headBranch,conclusion` then the 1.3
  job query on the most recent two.
- [ ] 1.5 Re-read the anchors this change depends on before editing `.github/workflows/ci.yml`:
  the workflow header note (`:8-13`), the promotion note above the job (`:262-288`), the job key
  and its rendered `name:` (`:288-289`), the job-level comment (`:290-292`), `needs: gate` (`:293`)
  and the fast-path `if:` (`:294`), and the two step-level dependency-PR skips (`:333-335`,
  `:340-341`). Confirm the `if:` on `live-db-tests` is byte-identical to the one on
  `build-and-test`, `coverage` and `aot-probe` — that identity is the whole merge-queue-safety
  argument (design D3).

**Phase A acceptance:** 1.2 prints exactly the five contexts without `Live-DB Tests (Postgres)`;
1.3 prints job+both-steps `success` on all five runs; 1.5 shows the four `if:` guards identical. No
file and no ruleset has been touched yet.

## 2. Phase B — Critical components (one focused subagent each)

### 2A. Tell the truth in the workflow — `.github/workflows/ci.yml` (comments only)

Do this **before** 2B. The comment edit is what the PR reviews; the ruleset edit is a
repository-configuration act that must land on a branch already proven green (design D2).

- [ ] 2.1 Rewrite the closing paragraph of the promotion note (`ci.yml:284-287`), which currently
  reads *"Gating is NOT the same as required: this check-run is still absent from the
  required-checks ruleset (17662679). Adding it there is a separate, deliberate repo-config change
  — a red job here is visible on the PR and blocks nothing automatically."* Replace it with the
  completed two-step record: the lane is now a **required** context on ruleset `17662679`; name the
  two promotion steps and their evidence (gating on runs `32480208475` + `32484193801` after the
  fixture fix in #255; required after the merge-queue runs `35542449427` / `35524372439` /
  `35517549350` held green a month later); and cite the archived change and task this discharges
  (`openspec/changes/archive/2026-07-05-live-postgres-ci-lane`, task 2.2) plus this change name.
  Do **not** rewrite the rest of the note — the readiness-race history and the
  `parallelizeTestCollections` measurement above it stay verbatim.
- [ ] 2.2 Rewrite the job-level comment (`ci.yml:290-292`). It currently says *"Gating …, but still
  not a required check. Gated on the docs-only fast path too — safe per ADR-0016 §4 Platform row: a
  **non-required** job skipping on a docs diff strands nothing."* The conclusion survives; the
  reason does not. State instead that the job is a required context and that the fast-path skip is
  safe because a **skipped required check satisfies branch protection** (verbara-meta/ADR-0016
  §1), the same way it already does for `build-and-test`, `coverage` and `aot-probe`, which carry
  the identical guard and are already required.
- [ ] 2.3 Reinforce the two step-level dependency-PR skip comments (`ci.yml:333-335` and
  `:340-341`) from rationale into invariant: now that the context is required, a **job**-level skip
  would collapse it and leave such PRs unmergeable, so the skip MUST stay at step level. Keep the
  existing `if:` expressions byte-identical — this task edits comments only.
- [ ] 2.4 Update the workflow header note (`ci.yml:10-13`), which describes the lane as *"gating
  since the readiness-probe fix graduated it, though still not a required check"*, so the parenthetical
  no longer contradicts the ruleset.
- [ ] 2.5 Change **nothing executable**. No `name:`, `needs:`, `if:`, `run:`, `timeout-minutes:`,
  `uses:`, trigger or `concurrency` edit anywhere in the file. In particular the rendered
  `name: Live-DB Tests (Postgres)` (`:289`) MUST NOT change — it is the required context string
  2B installs, and renaming it in the same change would orphan the ruleset entry.

### 2B. Install the required context — repository configuration, not a file

- [ ] 2.6 Apply the ruleset change by **read-modify-write**, never a hand-written body: `GET` the
  ruleset, append `{"context": "Live-DB Tests (Postgres)"}` to the `required_status_checks` rule's
  `parameters.required_status_checks` array, and `PUT` back a body carrying `name`, `target`,
  `enforcement`, `conditions`, `bypass_actors` and the full `rules` array. The ruleset also holds
  `deletion`, `non_fast_forward`, `pull_request` and `merge_queue` rules; a PUT that omits them
  drops them silently.

  ```sh
  RS=$(mktemp) && NEW=$(mktemp)
  gh api repos/:owner/:repo/rulesets/17662679 > "$RS"
  python3 - "$RS" "$NEW" <<'PY'
  import json, sys
  d = json.load(open(sys.argv[1]))
  CTX = "Live-DB Tests (Postgres)"
  for r in d["rules"]:
      if r["type"] == "required_status_checks":
          cs = r["parameters"]["required_status_checks"]
          assert not any(c["context"] == CTX for c in cs), "already required"
          cs.append({"context": CTX})
  body = {k: d[k] for k in ("name", "target", "enforcement", "conditions", "bypass_actors", "rules")}
  json.dump(body, open(sys.argv[2], "w"))
  PY
  gh api --method PUT repos/:owner/:repo/rulesets/17662679 --input "$NEW"
  ```

- [ ] 2.7 Copy the context string from the job's rendered `name:` (`ci.yml:289`) verbatim — it is
  `Live-DB Tests (Postgres)`, **not** the job key `live-db-tests`. A context that matches no
  check-run never reports and blocks every PR indefinitely (design D2, risk table row 1).
- [ ] 2.8 Append only. Do not reorder, rename or drop an existing context, and do not touch
  `strict_required_status_checks_policy` (stays `false`, design Open Questions),
  `do_not_enforce_on_create`, the bypass actors, or any non-status-check rule.
- [ ] 2.9 Sequencing gate — run 2.6 only **after** this branch's CI has reported green (task 4.4).
  A ruleset change takes effect immediately and repo-wide, including on this PR; applying it while
  this branch's run is still in flight makes the change the first casualty of its own gate.

**Phase B acceptance:** `git diff --stat` lists exactly one file, `.github/workflows/ci.yml`;
`git diff -- .github/workflows/ci.yml` contains only comment lines (every added/removed line starts
with `#` after its indentation);
`python3 -c "import yaml,sys; a=yaml.safe_load(open('.github/workflows/ci.yml')); print(sorted(a['jobs']))"`
still prints the eight job keys, and
`gh api repos/:owner/:repo/rulesets/17662679 --jq '[.rules[].type]'` still prints all five rule
types with `required_status_checks` now listing six contexts.

## 3. Phase C — Integration (batch)

- [ ] 3.1 Add the `CHANGELOG.md` `[Unreleased]` entry under a `### Changed — CI` heading: the
  Live-DB lane is now a **required** status check on `main-protection`; the two-step
  evidence-gated route that got it there; what a skipped lane means on a docs-only or
  dependency-bot PR (it satisfies protection — it is not a regression); and that a transient red
  is remedied by a re-run, not by reverting the promotion. Cite
  `decision_ref: verbara-meta/ADR-0023` and the archived change this harvests from
  (`2026-07-05-live-postgres-ci-lane`, task 2.2). Leave the `(#N)` citation for close-out.
- [ ] 3.2 Do **not** bump `Directory.Build.props` `<PackageVersion>`. Platform cuts versions on an
  explicit `chore(release): prep vX.Y.Z` commit; CI-only changes ride the next train as
  `[Unreleased]` entries.
- [ ] 3.3 Record in the PR body that the ruleset edit is a repository-configuration act performed
  outside the diff, so reviewing the diff alone does not show it — and that it took effect
  immediately and repo-wide the moment it was applied, on every open PR, not just on the next one.

**Phase C acceptance:** `git diff --stat` lists exactly two files (`.github/workflows/ci.yml` and
`CHANGELOG.md`); `git diff -- src tests docker Directory.Packages.props Directory.Build.props
openspec/specs scripts` is empty.

## 4. Verification

- [ ] 4.1 `dotnet build Verbara.Platform.slnx -c Release` completes with **zero warnings**
  (`TreatWarningsAsErrors=true`, `WarningLevel=9999`). Nothing in this change compiles, so this is
  a no-regression run.
- [ ] 4.2 `dotnet test` green — no test is added or changed by this change, so this is a
  no-regression run over the existing suite.
- [ ] 4.3 `openspec validate --all --strict --no-interactive` green, and
  `openspec validate --archived --no-interactive` still at rc=0.
- [ ] 4.4 **CI green on this PR** — a `.github/**` diff is not docs-only under the repo's
  classifier, so the full gate set runs: `Build + Unit Tests (Release)`, `Coverage Ratchet`,
  `OpenSpec Validate`, `AOT Publish (Api)`, `Invariant Gates` — plus `Live-DB Tests (Postgres)`
  itself, which must be green **before** 2.6 installs it as required (task 2.9).
- [ ] 4.5 Re-read the ruleset after 2.6 and confirm six contexts, the five originals plus
  `Live-DB Tests (Postgres)`, and that `[.rules[].type]` still lists `deletion`,
  `non_fast_forward`, `pull_request`, `required_status_checks` and `merge_queue`.
- [ ] 4.6 Confirm the context actually reports — the failure mode a ruleset read cannot catch. On
  this PR (or the next open one), `gh pr view <n> --json statusCheckRollup` must list a check named
  exactly `Live-DB Tests (Postgres)`, and `gh pr view <n> --json mergeStateStatus` must not report
  the PR as blocked by a missing context.
- [ ] 4.7 Prove the docs-only path is not stranded, on a real PR rather than by argument: on the
  next docs-only PR, confirm the lane reports `skipped` **and** the PR stays mergeable. Record that
  PR number here. This cannot be verified from this PR, whose `.github/**` diff is never classified
  docs-only.
- [ ] 4.8 Prove the dependency-bot path is not blocked: on the next automated dependency PR,
  confirm `Live-DB Tests (Postgres)` reports (its two test steps skipped, the job not skipped) and
  the PR is mergeable. Record that PR number here. Also post-merge-only.

## 5. Out of scope (record, do not implement)

- [ ] 5.1 Confirm `.github/workflows/release.yml` is untouched (`git diff --exit-code --
  .github/workflows/release.yml`). The post-release smoke job's own promotion is owned by the open
  change `promote-community-smoke-to-gating` (PR #221), which harvests archived task 3.2 of
  `2026-07-26-license-gated-engine-health-degraded`.
- [ ] 5.2 Confirm no other context was promoted — `Coverage Script Tests`, `Analyze (C#)`,
  `Dependency Review` and `CodeQL` are all still absent from the ruleset's required list.
- [ ] 5.3 Confirm the docs-only fast path itself is unchanged: `git diff --exit-code --
  scripts/ci/classify-docs-only.sh`, and the `gate` job's block in `ci.yml` is comment-identical
  and content-identical.
- [ ] 5.4 Confirm no ADR was created — this change cites `verbara-meta/ADR-0023` (harvest
  provenance) and `verbara-meta/ADR-0003` (the governing CI-gating standard) rather than adding a
  `docs/decisions/` file (design D6).
- [ ] 5.5 Confirm the lane's runtime is untouched: `git diff --exit-code -- tests/` and no change to
  `timeout-minutes`, the container setup steps, or either `dotnet test` invocation.
- [ ] 5.6 Leave `ci.yml:364`'s dangling `ADR-0038` citation alone — `docs/decisions/` has no
  ADR-0038 (the highest is `0037-canonical-rbac-permission-vocabulary.md`). Found while reading the
  file; it concerns the `Coverage Ratchet` job's check-name contract, not this lane, and fixing it
  here would widen the diff. Recorded so it is not lost.
