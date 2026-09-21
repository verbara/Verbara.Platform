# Design — promote-live-db-lane-to-required

## Context

`openspec/changes/archive/2026-07-05-live-postgres-ci-lane` built the Live-DB CI lane and
deliberately left it **informational**. Its task 1.2 recorded that grounding decision; its task 2.2
("Wire the job into branch protection per the 1.2 decision") was left unticked as *"N/A for now …
revisit at the promotion trigger documented in design.md"*. The promotion was therefore designed as
**two steps with an evidence gate between them**, not as a single act.

Step one has shipped. PR #257 (`ci: promote the Live-DB lane from report-only to gating`) dropped
`continue-on-error` after the fixture fix in PR #255 (archived change
`2026-08-21-fix-testcontainers-tcp-readiness`) removed the Postgres bootstrap-restart race, and it
gated that promotion on two green runs read at **step** level — the only honest reading while a job
is `continue-on-error`, because such a job reports `success` at the badge even when its steps fail.

Step two — the ruleset entry — is what this change performs. It is the residue of task 2.2, harvested
per verbara-meta/ADR-0023 rather than ticked, because the work genuinely has not been done.

## Goals / Non-Goals

**Goals**

- Make `Live-DB Tests (Postgres)` a required status check on the `main-protection` ruleset, so a red
  live-DB lane blocks a merge instead of merely annotating it.
- Leave behind a note in `.github/workflows/ci.yml` that is *true after* the change, replacing the
  paragraph that currently asserts the opposite.
- Record, with evidence, the three operational consequences of the promotion, so the next person
  reading a skipped or red required check does not mistake a designed behaviour for a regression.

**Non-Goals**

- Changing what the lane runs, how long it waits, or what it asserts. No step, `needs:`, `if:`,
  trigger or timeout is touched; `Storage.Postgres.Tests` and `Identity.Redis.Tests` are consumed
  exactly as they are.
- Promoting any other non-required context.
- Touching `.github/workflows/release.yml`, which belongs to `promote-community-smoke-to-gating`.
- Any production-source, test, Docker or package-pin edit. The AOT constraints (no reflection,
  `[JsonSerializable]` source-gen for every DTO, no Dapper — `Verbara.Sdk.Data.Npgsql` only,
  Platform/ADR-0022) are untouched by construction: this change compiles nothing new.

## Decisions

### D1 — Promote to *required*, rather than leaving the lane gating-only

**Decision:** add the context to ruleset `17662679`.

The archived change's promotion trigger is satisfied on arm (a): the fixture-level fix landed, and
the lane then ran green on consecutive real PRs and has stayed green on the merge queue a month
later (the run ids are enumerated in `proposal.md` and re-derived in Phase A). Arm (b) — an
independent flake re-measurement — was the alternative route and is not needed.

**Why gating alone is not enough.** A gating, non-required job turns red on the PR and blocks
nothing. The lane exists because persistence logic that depends on real Postgres semantics can pass
against the `Storage.InMemory` mirror and still be wrong; an advisory red on exactly that class of
defect is a signal with no consequence. Making it required is the difference between the lane
*reporting* a divergence and the lane *preventing* one.

**Why now and not at day one.** Promoting on the strength of the fix alone would have risked
intermittently blocking unrelated PRs — the outcome report-only existed to avoid. The two-step
route was the point, and this is its second step.

### D2 — A ruleset PUT, applied after the branch is green — not a file edit

The required-checks list lives in GitHub repository configuration (`main-protection`, id
`17662679`, target `branch`, enforcement `active`), not in any tracked file. It is applied with
`gh api` by reading the current ruleset, **appending** one `{"context": …}` entry to the
`required_status_checks` rule's parameters, and PUTting the result back. Read-modify-write, never a
hand-written body: the ruleset also carries `deletion`, `non_fast_forward`, `pull_request` and
`merge_queue` rules, and a PUT that omits them would silently drop them.

**The context string is the job's `name:` value, verbatim** — `Live-DB Tests (Postgres)`
(`ci.yml:289`) — not the job key `live-db-tests`. A mismatched context never reports and blocks
every PR indefinitely, which is why Phase C re-reads the ruleset and a real PR's check names rather
than trusting the PUT's response.

**Sequencing.** A ruleset change takes effect immediately and repo-wide, including on this change's
own PR. Phase B therefore applies it only **after** this branch's CI is green, so the PR is not
made to wait on a context that did not exist when its run started. Ordering the other way is the
one way this low-risk change can wedge its own merge.

### D3 — The docs-only fast path stays safe, because the job already has the required-job shape

This is the constraint that makes or breaks the promotion under a merge queue, and it was checked
mechanically rather than assumed.

`live-db-tests` carries `needs: gate` and the `if:` guard
`${{ !cancelled() && (needs.gate.result != 'success' || needs.gate.outputs.docs_only != 'true') }}`
— **byte-identical** to the guard on `build-and-test`, `coverage` and `aot-probe`. Those three are
*already* required contexts in ruleset `17662679`. So the promotion does not introduce a new shape:
it adds a fourth job of a shape this repo's ruleset and merge queue have been living with since the
fast path shipped.

The normative basis is verbara-meta/ADR-0016 §1 (a skipped required job satisfies protection,
which is what makes the job-level fast-path skip legal for required contexts at all) and its §4
per-repo matrix, whose Platform row anticipated exactly this move — it records the Live-DB lane as
non-required and notes it may be gated by the fast path too. The field evidence is this repo's own
docs PRs merging with heavy required jobs reporting `skipped`.

The job-level comment at `ci.yml:290-292` currently justifies the fast-path skip with *"safe per
ADR-0016 §4 Platform row: a **non-required** job skipping on a docs diff strands nothing"*. That
justification becomes wrong the moment the job is required — not the conclusion, which still holds,
but the reason. It is rewritten to the correct one: the skip is safe because a skipped required
check satisfies protection, the same way it already does for the three heavy required jobs beside it.

### D4 — The dependabot skip must stay STEP-level

The CI-load skip on this job is applied to the two test steps (`ci.yml:333-335` and `:340-341`),
not to the job. That is deliberate and its rationale is already written into the file: a job-level
skip collapses the check-run context, so a required check would never report on a bot PR and would
block it forever.

Under this change that comment stops being a stylistic preference and becomes an invariant. It is
recorded as a requirement in the spec delta, and Phase C asserts it mechanically (the job block
carries no top-level `if:` other than the fast-path guard, and both test steps keep theirs).

### D5 — Flake budget and rollback

A required check converts every red into a merge block, so the honest question is what reds this
lane actually produces.

The one non-test red on record is instructive: on PR #257, attempt 1 of run `32485793699` had
`Live-DB Tests (Postgres)` conclude `failure` at its *Build (Release, warnings-as-errors)* step
while all seven other jobs concluded `success`; attempt 2 of the same run was green with no code
change. The step that failed is the one immediately after the private-feed NuGet source is
configured, so an infrastructure hiccup on restore is the natural reading — **but the log for that
attempt is no longer retrievable, so this design does not assert a specific cause.** What is
verified is the shape: a transient, non-test, re-runnable failure in this job.

**Consequence, recorded so nobody re-litigates the promotion over it:** under a required context a
red of that shape blocks the PR until someone re-runs the job. The remedy is the re-run, and if the
shape recurs, a retry on the restore/build step. Walking the promotion back is not the remedy — the
lane's *test* record across PR, unrelated-PR and merge-queue runs is green.

**Rollback** is the inverse of D2's PUT: remove the one appended context. It is a single
repository-configuration call, needs no revert commit, and takes effect immediately.

### D6 — No ADR is authored

The durable decision here is not new: the archived change's `design.md` already specified the
promotion trigger and its two arms, and verbara-meta/ADR-0003 is the governing CI-gating and
branch-protection standard. This change *executes* a decision that was already recorded, so the
repo's "record any durable architectural decision as an ADR" rule is satisfied by citation rather
than by a new file. `decision_ref: verbara-meta/ADR-0023` carries the harvest's provenance.

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Context string mismatch — the required check never reports and blocks every PR | Low | Phase B copies the `name:` value verbatim from `ci.yml:289`; Phase C re-reads the ruleset **and** confirms the context appears on a real PR's check list before the change is considered applied |
| This PR blocked by its own new gate | Low | D2's sequencing: the ruleset PUT happens only after this branch's CI is green |
| A docs-only PR stranded by a skipped required check | Low | D3 — the job already carries the identical guard used by three existing required contexts; Phase C proves it on a docs-only diff |
| A dependabot PR blocked forever | Low | D4 — the skip is step-level and stays step-level; asserted in Phase C |
| Transient infrastructure red now blocks a merge | Medium | D5 — accepted and documented; remedy is a re-run, escalating to a restore/build-step retry, never a rollback of the promotion |
| The ruleset's other rules dropped by a careless PUT | Low | D2 — read-modify-write over the live ruleset, never a hand-written body; Phase C diffs the rule-type list before and after |

## Open Questions

- **Should `strict_required_status_checks_policy` be turned on for this context?** It is `false`
  today for the whole rule and stays `false`; with a merge queue the tentative merge result is
  already tested against the target branch, so requiring branches to be up to date first would add
  churn without adding signal. Not changed here, and not a blocker.
- **Should `Coverage Script Tests` follow?** It is the remaining always-run, non-required context
  and verbara-meta/ADR-0016 §3.3 discusses hosting classifier-guarding checks in a required
  always-run job. Out of scope here (see `proposal.md`); worth its own grounding if the fast-path
  classifier's own coverage is ever the concern.
