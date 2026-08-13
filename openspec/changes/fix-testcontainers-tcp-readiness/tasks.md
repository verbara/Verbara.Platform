> **Execution model (Platform convention):** Subagent-Driven Development with FCM batching —
> **Phase A** foundation (batch) → **Phase B** critical components (focused) → **Phase C**
> integration (batch). Groups 1/2/3 map to A/B/C.
>
> **Scope guard:** no file under `src/` is touched by this change. If implementing it seems to
> require a production edit, stop and re-open the design.

## 1. Phase A — Foundation (batch): the probe fix across every fixture

- [ ] 1.1 Apply the TCP-scoped readiness probe to the **16** remaining fixtures in
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
- [ ] 1.2 Apply the same fix to `tests/Verbara.Platform.Channels.Sms.Tests/CsatSmsCorrelatorFixture.cs`.
  **This one is in the required `Build + Unit Tests` job**, not the report-only lane — the fast lane
  excludes only `Storage.Postgres.Tests` and `Identity.Redis.Tests` (design D3, D6). Its exposure is
  lower (one container, and the race is worst under concurrent starts, which is consistent with that
  job never having been observed failing on it), but it is the same defect in a required check.
- [ ] 1.3 Carry the same short explanatory comment on every fixed fixture — the one
  `UserMfaEncryptionFixture` already has: `pg_isready` without `-h` probes the container's **internal
  Unix socket**, which reports ready seconds before the published TCP port the client actually dials,
  because the official entrypoint runs `initdb` against a temp server with `listen_addresses=''`.
  The next fixture author must copy the corrected shape, not the broken one.
- [ ] 1.4 **Verify mechanically, not by eye:**
  `grep -rn 'pg_isready", "-U", "postgres")' tests/ --include='*.cs'` MUST return zero hits, and
  `grep -rlc 'pg_isready' tests/ --include='*.cs' | wc -l` MUST still be 18.

## 2. Phase B — Critical: prove it actually fixed the race

- [ ] 2.1 Run `dotnet test tests/Verbara.Platform.Storage.Postgres.Tests` **at least five times
  consecutively** and record every result. The baseline to beat is a *varying* subset failing
  (21 → 115 → 174 across observed runs), so the acceptance criterion is not "it passed once" but
  **identical results across repeated runs, with zero `NpgsqlException` originating in a fixture's
  `InitializeAsync`**.
- [ ] 2.2 Run `dotnet test tests/Verbara.Platform.Channels.Sms.Tests` repeatedly too, since 1.2
  touched a required job.
- [ ] 2.3 If any `NpgsqlException` startup failure survives, **stop and re-open the design** — that
  is the D2 trigger to reconsider the `Testcontainers.PostgreSql` module's log-based double-ready
  strategy. Do not paper over a residual failure with a retry loop.
- [ ] 2.4 Record the measured before/after in the PR body — the numbers are the whole argument for
  promoting the lane later.

## 3. Phase C — Integration: the CI comment

- [ ] 3.1 Rewrite the `live-db-tests` job comment in `.github/workflows/ci.yml`. It currently
  documents the race as **open** and says "~13 Postgres fixtures" — the real count is **18** (17
  needed fixing). Leaving it would send the next reader to re-solve a solved problem with a wrong
  inventory.
- [ ] 3.2 In the same comment, correct the premise that container-backed tests are confined to the
  report-only lane (design D6). Four projects use Testcontainers — `Storage.Postgres.Tests`,
  `Identity.Redis.Tests`, `Channels.Sms.Tests`, `Mail.Tests` — and only the first two are excluded
  from the fast lane. State the true picture; do **not** relitigate the lane split here.
- [ ] 3.3 Do **not** remove `continue-on-error: true` in this PR. Promotion is gated on evidence
  (task 5).

## 4. `parallelizeTestCollections` — decide by measurement

- [ ] 4.1 With the probe fixed, measure `tests/Verbara.Platform.Storage.Postgres.Tests` repeatedly
  **with** `parallelizeTestCollections: false` and **without** it. Capture wall-clock and
  pass/fail stability for each, over several runs — a single green parallel run proves nothing about
  a timing race.
- [ ] 4.2 Remove the setting **only** if the parallel configuration is stable across repeated runs.
  If it is not, leave it and write down why (design D4). Either way the measurement goes in the PR
  body, not just the conclusion.
- [ ] 4.3 Apply the same decision to `Channels.Sms.Tests`'s `xunit.runner.json`, which carries the
  same setting for the same reason.
- [ ] 4.4 Leave `Identity.Redis.Tests` alone unless 4.1's measurement covers it — it is 34/34 green
  in every configuration and the Redis image has no analogous restart cycle.

## 5. Promotion to gating — a SEPARATE follow-up PR

- [ ] 5.1 After the fix lands, observe `Live-DB Tests (Postgres)` on **two consecutive** runs —
  this PR's own run and the next unrelated PR's.
- [ ] 5.2 Only then, in its own PR, drop `continue-on-error: true` from **both** test steps in the
  `live-db-tests` job and cite the two green runs that justified it (design D5, the graduation
  discipline `released-image-smoke` already uses).
- [ ] 5.3 Update the job comment again at that point so it reflects a gating lane rather than a
  report-only one.

## 6. Verification

- [ ] 6.1 `dotnet build Verbara.Platform.slnx` — **zero warnings** (`TreatWarningsAsErrors`,
  `WarningLevel 9999`).
- [ ] 6.2 The CI unit lane green:
  `dotnet test Verbara.Platform.slnx --filter "FullyQualifiedName!~Storage.Postgres.Tests&FullyQualifiedName!~Identity.Redis.Tests"`
  — this now includes the `Channels.Sms.Tests` fixture touched in 1.2.
- [ ] 6.3 `tests/Verbara.Platform.Storage.Postgres.Tests` **fully green and repeatably so** (task
  2.1). This is the task that closes out `encrypt-mfa-secrets-at-rest` 6.2, which was closed at
  archive carrying this change as its destination.
- [ ] 6.4 Patch coverage is **not** expected to apply — this change touches only `tests/` and
  `.github/`, and `check-patch-coverage.py` scopes its liveness trip to instrumented projects, so a
  zero measurement here is honest rather than a mis-wiring. Confirm the gate reports that, rather
  than assuming it.
- [ ] 6.5 `openspec validate --all --strict` green.
- [ ] 6.6 CI green on the PR, with the `Live-DB Tests (Postgres)` job's **underlying run** green —
  not merely its `continue-on-error` check-run. Read the log, not the badge.
