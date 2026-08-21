> **Execution model (Platform convention):** Subagent-Driven Development with FCM batching —
> **Phase A** foundation (batch) → **Phase B** critical components (focused) → **Phase C**
> integration (batch). Groups 1/2/3 map to A/B/C.
>
> **Scope guard:** no file under `src/` is touched by this change. If implementing it seems to
> require a production edit, stop and re-open the design.

## 1. Phase A — Foundation (batch): the probe fix across every fixture

- [x] 1.1 Apply the TCP-scoped readiness probe to the **16** remaining fixtures in
  `tests/Verbara.Platform.Storage.Postgres.Tests`, changing
  `UntilCommandIsCompleted("pg_isready", "-U", "postgres")` to
  `UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1")`:
  `Seeds/PostgresRbacFixture`, `Stores/AgentStoreAutoAnswerFixture`, `Stores/AiSuggestionStoreFixture`,
  `Stores/ApiKeyStoreLastUsedFixture`, `Stores/AuditEntriesNormalizationFixture`,
  `Stores/ConversationVoiceLinkFixture`, `Stores/CreditLedgerStoreFixture`,
  `Stores/DialerLicenseAuditFixture`, `Stores/InvoiceStoreFixture`, `Stores/IpAllowlistFixture`,
  `Stores/MigrationsFixture`, `Stores/TenantAuthConfigEncryptionFixture`,
  `Stores/TenantLlmConfigSeedFixture`, `Stores/TypificationCorrectionAuditFixture`,
  `Stores/TypificationStoreFixture`, `Stores/UsageRecordStoreFixture`.
  `Stores/UserMfaEncryptionFixture` already has it — leave it, and use it as the reference shape.
- [x] 1.2 Apply the same fix to `tests/Verbara.Platform.Channels.Sms.Tests/CsatSmsCorrelatorFixture.cs`.
  **This one is in the required `Build + Unit Tests` job**, not the report-only lane — the fast lane
  excludes only `Storage.Postgres.Tests` and `Identity.Redis.Tests` (design D3, D6). Its exposure is
  lower (one container, and the race is worst under concurrent starts, which is consistent with that
  job never having been observed failing on it), but it is the same defect in a required check.
- [x] 1.3 Carry the same short explanatory comment on every fixed fixture — the one
  `UserMfaEncryptionFixture` already has: `pg_isready` without `-h` probes the container's **internal
  Unix socket**, which reports ready seconds before the published TCP port the client actually dials,
  because the official entrypoint runs `initdb` against a temp server with `listen_addresses=''`.
  The next fixture author must copy the corrected shape, not the broken one.
- [x] 1.4 **Verify mechanically, not by eye:**
  `grep -rn 'pg_isready", "-U", "postgres")' tests/ --include='*.cs'` MUST return zero hits, and
  `grep -rlc 'pg_isready' tests/ --include='*.cs' | wc -l` MUST still be 18.

## 2. Phase B — Critical: prove it actually fixed the race

- [x] 2.1 Run `dotnet test tests/Verbara.Platform.Storage.Postgres.Tests` **at least five times
  consecutively** and record every result. The baseline to beat is a *varying* subset failing
  (21 → 115 → 174 across observed runs), so the acceptance criterion is not "it passed once" but
  **identical results across repeated runs, with zero `NpgsqlException` originating in a fixture's
  `InitializeAsync`**.
- [x] 2.2 Run `dotnet test tests/Verbara.Platform.Channels.Sms.Tests` repeatedly too, since 1.2
  touched a required job.
- [x] 2.3 If any `NpgsqlException` startup failure survives, **stop and re-open the design** — that
  is the D2 trigger to reconsider the `Testcontainers.PostgreSql` module's log-based double-ready
  strategy. Do not paper over a residual failure with a retry loop.
- [x] 2.4 Record the measured before/after in the PR body — the numbers are the whole argument for
  promoting the lane later.

> **2.4 result — shipped in PR #255's body**, as the measurement table that D4 requires:
> `Storage.Postgres.Tests` before/serialized over 4 runs → `27 / 136 / 11 / 207` failures;
> after with `parallelizeTestCollections: false` over 5 runs → `262/262` every run, ~79s;
> after with `parallelizeTestCollections: true` over 6 runs → `262/262` every run, ~31–34s.
> Those are the numbers cited back in 5.1/5.2 to justify the promotion, so the argument is
> on the record rather than reconstructed after the fact.

## 3. Phase C — Integration: the CI comment

- [x] 3.1 Rewrite the `live-db-tests` job comment in `.github/workflows/ci.yml`. It currently
  documents the race as **open** and says "~13 Postgres fixtures" — the real count is **18** (17
  needed fixing). Leaving it would send the next reader to re-solve a solved problem with a wrong
  inventory.
- [x] 3.2 In the same comment, correct the premise that container-backed tests are confined to the
  report-only lane (design D6). Four projects use Testcontainers — `Storage.Postgres.Tests`,
  `Identity.Redis.Tests`, `Channels.Sms.Tests`, `Mail.Tests` — and only the first two are excluded
  from the fast lane. State the true picture; do **not** relitigate the lane split here.
- [x] 3.3 Do **not** remove `continue-on-error: true` in this PR. Promotion is gated on evidence
  (task 5).

## 4. `parallelizeTestCollections` — decide by measurement

- [x] 4.1 With the probe fixed, measure `tests/Verbara.Platform.Storage.Postgres.Tests` repeatedly
  **with** `parallelizeTestCollections: false` and **without** it. Capture wall-clock and
  pass/fail stability for each, over several runs — a single green parallel run proves nothing about
  a timing race.
- [x] 4.2 Remove the setting **only** if the parallel configuration is stable across repeated runs.
  If it is not, leave it and write down why (design D4). Either way the measurement goes in the PR
  body, not just the conclusion.
- [x] 4.3 Apply the same decision to `Channels.Sms.Tests`'s `xunit.runner.json`, which carries the
  same setting for the same reason.
- [x] 4.4 Leave `Identity.Redis.Tests` alone unless 4.1's measurement covers it — it is 34/34 green
  in every configuration and the Redis image has no analogous restart cycle.

  **Measured (2026-08-21, 24-core host, Docker local):**

  | Project | Config | Runs | Result | Wall-clock |
  |---|---|---|---|---|
  | Storage.Postgres.Tests | *before the probe fix*, serialized | 4 | 27 / 136 / 11 / 207 failures | — |
  | Storage.Postgres.Tests | after fix, `parallelizeTestCollections: false` | 5 | 262/262 every run | ~79s |
  | Storage.Postgres.Tests | after fix, `parallelizeTestCollections: true` | 6 | 262/262 every run | ~31-34s |
  | Channels.Sms.Tests | after fix, `false` | 3 | 48/48 every run | ~4s |
  | Channels.Sms.Tests | after fix, `true` | 4 | 48/48 every run | ~3-4s |

  **Decision:** parallel for both. Postgres gains 2.4x wall-clock at 24-way concurrency — *more*
  simultaneous container starts than a 2-4 core CI runner produces, so the local run is the harsher
  test, not the softer one. Sms gains nothing measurable (one fixture) but carries the setting for
  the same now-fixed reason, so leaving it would preserve a stale workaround the next reader would
  have to re-litigate. `Identity.Redis.Tests` untouched per 4.4.

  **Deviation from 4.2's wording:** the setting is set to `true` explicitly rather than deleted.
  Deleting the key would leave the file inert, and deleting the *file* would leave a stale
  `bin/**/xunit.runner.json` from a previous build still being read at runtime — a genuine local
  footgun, since `CopyToOutputDirectory` does not remove what it previously copied. An explicit
  `true` is functionally identical to absence (it is xUnit's default) and records that the value
  was measured rather than defaulted.

## 5. Promotion to gating — a SEPARATE follow-up PR

- [x] 5.1 After the fix lands, observe `Live-DB Tests (Postgres)` on **two consecutive** runs —
  this PR's own run and the next unrelated PR's.
- [x] 5.2 Only then, in its own PR, drop `continue-on-error: true` from **both** test steps in the
  `live-db-tests` job and cite the two green runs that justified it (design D5, the graduation
  discipline `released-image-smoke` already uses).
- [x] 5.3 Update the job comment again at that point so it reflects a gating lane rather than a
  report-only one.

## 6. Verification

- [x] 6.1 `dotnet build Verbara.Platform.slnx` — **zero warnings** (`TreatWarningsAsErrors`,
  `WarningLevel 9999`).
- [x] 6.2 The CI unit lane green:
  `dotnet test Verbara.Platform.slnx --filter "FullyQualifiedName!~Storage.Postgres.Tests&FullyQualifiedName!~Identity.Redis.Tests"`
  — this now includes the `Channels.Sms.Tests` fixture touched in 1.2.
- [x] 6.3 `tests/Verbara.Platform.Storage.Postgres.Tests` **fully green and repeatably so** (task
  2.1). This is the task that closes out `encrypt-mfa-secrets-at-rest` 6.2, which was closed at
  archive carrying this change as its destination.
- [x] 6.4 Patch coverage is **not** expected to apply — this change touches only `tests/` and
  `.github/`, and `check-patch-coverage.py` scopes its liveness trip to instrumented projects, so a
  zero measurement here is honest rather than a mis-wiring. Confirm the gate reports that, rather
  than assuming it.
- [x] 6.5 `openspec validate --all --strict` green.

> **5.1 evidence — two consecutive green runs, read at STEP level.** A `continue-on-error`
> job reports `success` even when its steps fail, so the check-run badge proves nothing here;
> these are the `steps[].conclusion` values pulled from the Actions API, not the badge.
> - **Run 1/2 — `32480208475`**, branch `fix/testcontainers-tcp-readiness` (this change's own
>   PR #255): `Live-DB tests — Storage.Postgres.Tests=success`,
>   `Live-DB tests — Identity.Redis.Tests=success`.
> - **Run 2/2 — `32484193801`**, branch `fix/local-kind-datetimeoffset` (PR #256, an
>   **unrelated** change — the requirement was the next unrelated PR's run, not a re-run of
>   this one): both steps `success`, job 3m40s.
>
> Corroborated by two post-merge merge-queue runs on `main` after #255 landed —
> `32482194226` (pr-251) and `32482656879` (pr-254) — both `success+success` at step level.
> Four green step-level observations in total, none of them a repeat of the same tree.

> **5.2 / 5.3 shipped in `ci/promote-live-db-to-gating`.** `continue-on-error: true` removed
> from **both** test steps (`grep -c continue-on-error` over the `live-db-tests` job → 0;
> confirmed by parsing the YAML, not by reading the diff). Three stale comments rewritten
> rather than one:
> - the **file header** (`ci.yml:10-13`) still described the lane as "report-only pending
>   promotion";
> - the **block above the job**, whose `STILL REPORT-ONLY` paragraph was replaced with the
>   promotion record — the four run IDs above, why step-level reading was necessary, and the
>   D5 rationale;
> - the **job-level comment**, trimmed to a pointer at that block so the evidence lives in
>   exactly one place instead of being duplicated.
>
> The rewrite states explicitly that **gating is not the same as required**: the check-run is
> still absent from ruleset `17662679`, so a red job is visible on the PR and blocks nothing
> automatically. Adding it to the required list is a separate, deliberate repo-config change
> and is *not* part of this promotion — D5 asks only for `continue-on-error` to come off.
>
> **Observed cost of gating, on the very first gated run — recorded rather than forgotten.**
> PR #257's own `Live-DB Tests (Postgres)` went **red** on run `32485793699`, and the failing
> step was **`Build (Release, warnings-as-errors)`** — *not* either test step; both of those
> were `skipped`. Cause: GitHub Packages answered **HTTP 500** ("Operation could not be
> completed within the specified time") downloading `Verbara.Sdk.Pro.Push.2.14.1-pro`. A
> transient private-feed flake, unrelated to Testcontainers and unrelated to this change —
> `Build + Unit Tests (Release)` passed in the same run, so it was per-job. `gh run rerun
> --failed` came back green with both test steps `success`.
>
> This is the honest trade-off the promotion buys: **a transient feed 500 in this job now
> produces a red PR instead of being swallowed.** It is tolerable precisely because gating is
> not the same as required — the check-run stays out of ruleset `17662679`, so a red job is
> visible and blocks nothing automatically. If private-feed 500s prove frequent rather than
> occasional, the fix is a restore retry on the build step, not walking the promotion back.

> **6.4 result — the gate reported it, it was not assumed.** From PR #255's Coverage Ratchet
> job, step `Enforce patch coverage (diff-cover)`:
> `Patch coverage: no measurable cobertura lines in this diff (no instrumented line added). floor 85.0% — n/a, pass.`
> That is `check-patch-coverage.py` explicitly recognising an uninstrumented diff, which is
> exactly the honest-zero path — not a silent skip and not a mis-wiring.

> **6.6 result.** PR #255's CI was green with the Live-DB job's **underlying steps** green,
> read from the API rather than the badge (run `32480208475`, both test steps `success`) —
> the distinction 6.6 was written to force.

- [x] 6.6 CI green on the PR, with the `Live-DB Tests (Postgres)` job's **underlying run** green —
  not merely its `continue-on-error` check-run. Read the log, not the badge.
