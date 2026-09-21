# live-db-ci-lane — Delta

Answers the question this capability deliberately left open. Its fourth requirement —
*"Required-check status is decided at grounding, not assumed here"* — was a placeholder: the
originating change declined to presume whether the lane would be required or informational, and
recorded the answer as owed. That answer is now due, because the promotion trigger the originating
change wrote down has fired.

This delta MODIFIES that placeholder into the recorded decision, and ADDS two requirements: the
lane is a required status check, and the skips that keep CI cheap MUST keep the required context
*reporting* rather than collapsing it. It changes nothing about what the lane runs, what it
asserts, or how long it waits.

## ADDED Requirements

### Requirement: The live-DB lane is a required status check

The live-DB lane's check-run context MUST be present in the default branch's protection ruleset as
a required status check, so that a failing lane **blocks** the merge rather than annotating it. A
gating job — one that reports its own failure faithfully — SHALL NOT be treated as satisfying this
requirement: reporting and blocking are different guarantees, and only the ruleset entry delivers
the second.

The required context SHALL be the lane job's rendered check-run name, matched verbatim. A context
string that does not correspond to a check-run the workflow actually reports MUST NOT be added,
because such an entry never reports and blocks every pull request indefinitely.

The ruleset entry SHALL be added by appending to the existing required-status-check list, leaving
every other rule in the ruleset — deletion protection, non-fast-forward, pull-request and
merge-queue rules — unchanged.

#### Scenario: A failing live-DB lane blocks the merge

- **GIVEN** a pull request whose changes are not classified docs-only
- **AND** the live-DB lane's check-run context is a required status check on the default branch
- **WHEN** the lane concludes `failure` — whether from a failing `Storage.Postgres.Tests` or
  `Identity.Redis.Tests` assertion, or from an earlier step in the same job
- **THEN** the pull request is not mergeable until that check reports success
- **AND** the failure is not merely visible on the pull request while it merges anyway

#### Scenario: A passing live-DB lane satisfies protection

- **GIVEN** a pull request whose changes are not classified docs-only
- **WHEN** the live-DB lane runs and both live-DB test steps conclude `success`
- **THEN** the lane's required context reports success and does not block the merge

#### Scenario: The required context matches a check-run the workflow reports

- **GIVEN** the required-status-check list of the default branch's ruleset
- **WHEN** the live-DB lane's entry is compared against the check-run names produced by a real
  pull-request run of the CI workflow
- **THEN** the entry matches one of them exactly, so the context reports on every pull request
  rather than blocking indefinitely

#### Scenario: Promotion leaves the rest of the ruleset intact

- **GIVEN** a ruleset carrying deletion, non-fast-forward, pull-request, required-status-check and
  merge-queue rules
- **WHEN** the live-DB lane's context is added to the required-status-check list
- **THEN** the ruleset still carries all five rule types afterwards
- **AND** the previously required contexts are all still present, with one new entry appended

### Requirement: Cost-saving skips MUST keep the required context reporting

Any skip applied to the live-DB lane for cost reasons MUST preserve the lane's check-run context on
the pull request. A skip that removes the context entirely SHALL NOT be used once the lane is
required, because a required context that never reports blocks the pull request permanently.

Two skips exist and their levels are therefore normative, not stylistic:

- The **docs-only fast path** SHALL remain a job-level condition of the same shape already used by
  the other heavy required jobs in this workflow. A required job skipped under branch protection
  satisfies protection, which is what makes a job-level skip legal here (verbara-meta/ADR-0016 §3.3,
  with the per-repo applicability recorded in its §4 matrix).
- The **automated-dependency-PR CI-load skip** SHALL remain **step**-level, applied to the live-DB
  test steps and never to the job. A job-level skip would collapse the check-run context and leave
  such pull requests unmergeable.

#### Scenario: A docs-only pull request is not stranded

- **GIVEN** a pull request whose diff is classified docs-only
- **AND** the live-DB lane's context is a required status check
- **WHEN** the workflow runs and the lane is skipped by the docs-only fast path
- **THEN** the pull request remains mergeable, exactly as it does for the other heavy required jobs
  guarded by the same condition
- **AND** no live-DB container is started for that pull request

#### Scenario: An automated dependency pull request is not blocked forever

- **GIVEN** a pull request opened by the automated dependency updater, whose diff is not docs-only
- **WHEN** the workflow runs and the live-DB test steps are skipped for CI load
- **THEN** the live-DB lane still reports its check-run context with a non-failing conclusion
- **AND** the pull request is mergeable

#### Scenario: A merge-queue run reports the required context

- **GIVEN** a pull request enqueued in the merge queue
- **WHEN** the workflow runs against the tentative merge result
- **THEN** the live-DB lane runs its test steps rather than skipping them for CI load
- **AND** its required context reports on that run

## MODIFIED Requirements

### Requirement: Required-check status is decided at grounding, not assumed here

The branch-protection status of this lane (required vs. informational) SHALL be a grounding-time
decision, recorded with its rationale and consistent with the ecosystem CI-gating standard
(verbara-meta/ADR-0003). The decision MUST NOT be presumed by a backlog proposal, and once taken it
MUST be recorded where the lane is defined rather than only in a change document.

Where the decision is *informational-then-promoted*, the deferral SHALL name a concrete promotion
trigger, and the promotion SHALL be gated on evidence against that trigger rather than on elapsed
time or on the mere presence of a fix. Evidence for a lane that is still non-blocking SHALL be read
at the level that reports faithfully — the job's own conclusion and its steps — never at a
workflow-level conclusion that a non-blocking job cannot influence.

The decision for this lane is now taken and is **required**, reached by the
informational-then-promoted route in two evidence-gated steps: informational at introduction; then
gating once a fixture-level fix for the container bootstrap-restart race landed and the lane ran
green on consecutive real pull requests; then required, after the lane additionally held green on
merge-queue runs over the following month. Both promotions were evidence-gated in that order, and
neither was taken on the strength of the fix alone.

#### Scenario: Apply-time grounding records the required-check decision

- **GIVEN** this lane is introduced or its protection status is revisited
- **WHEN** the apply-time design is finalized
- **THEN** the required-check status is recorded — required from day one, or
  informational-then-promoted — together with its rationale, consistent with the ecosystem
  CI-gating standard

#### Scenario: A deferred promotion carries a trigger, not an open end

- **GIVEN** a grounding decision of informational-then-promoted
- **WHEN** that decision is recorded
- **THEN** it names the concrete condition that would promote the lane
- **AND** the outstanding promotion is tracked as work rather than closed as not-applicable

#### Scenario: Evidence for promoting a non-blocking lane is read at job level

- **GIVEN** a lane that is still non-blocking, so the workflow-level conclusion cannot reflect its
  failures
- **WHEN** the promotion trigger is evaluated
- **THEN** the evidence cited is the lane job's own conclusion and its step conclusions on named
  runs
- **AND** a workflow-level conclusion is not accepted as evidence that the lane passed

#### Scenario: The recorded decision is visible where the lane is defined

- **GIVEN** the promotion has been applied
- **WHEN** a maintainer reads the lane's definition in the CI workflow
- **THEN** the note beside it states the lane's current protection status correctly
- **AND** no surviving note asserts that the lane is not a required check

## Architectural Risk

- **Level:** LOW
- **Affected:** repository configuration (the default branch's protection ruleset gains one
  required status-check context) and comments in `.github/workflows/ci.yml`. No job, step,
  `needs:`, `if:`, trigger or timeout changes; nothing under `src/`, `tests/`, `docker/`,
  `Directory.Packages.props` or `Directory.Build.props`; no package pin moves and no cross-repo
  impact along `Sdk → Sdk.Pro → Platform ← Platform.Web`. Because nothing is compiled or
  serialized, the Native-AOT constraints (Platform/ADR-0022) are untouched by construction. The
  blast radius is every pull request to the default branch, which is why the failure modes below
  are each mechanically checked rather than argued.
- **Mitigation:** the promotion is the second half of a two-step, evidence-gated route the
  originating change designed, not a new posture: the lane already ran green at step level on the
  fixture-fix pull request, on an unrelated pull request, and on three merge-queue runs a month
  later. The three ways a required check can wedge a repository are each closed and asserted —
  a mismatched context string (the entry is copied verbatim from the job's rendered name and
  re-verified against a real pull request's check names after the ruleset edit); a docs-only pull
  request stranded by a skipped required check (the lane already carries the identical fast-path
  guard used by three jobs that are *already* required contexts, and a skipped required check
  satisfies protection); and an automated-dependency pull request blocked forever (its CI-load skip
  is step-level and is now normative at that level). The ruleset edit is applied by appending to the
  live ruleset rather than replacing it, so the other rules cannot be dropped, and it is applied only
  after the promoting branch is itself green so the change cannot block its own merge. Residual
  exposure is a transient non-test failure in the job turning into a merge block; that is accepted,
  documented, and remedied by a re-run rather than by reverting the promotion. Rollback is removing
  the one appended context — a single configuration call, no revert commit, effective immediately.
