# Tasks — credit-grant-lazy-mint-rollover

## 1. Grounding

- [x] 1.1 Confirm the exact balance-read call-sites on the enforcement/readout paths and the
      grant-existence lookup cost (index usage)

      Confirmed call-sites: `DefaultQuotaEnforcementService.CheckAiCreditQuotaLedgerAsync` (enforcement,
      behind `LedgerEnforcementEnabled`) and `CreditLedgerEndpoints.GetBalance` /
      `CreditLedgerEndpoints.GetRemainingBySource` (readout — both surface balance/derived-balance data;
      `GetEntries` lists raw entries, not a balance, so out of scope). No existing grant-existence lookup
      method on `ICreditLedgerStore` — added `HasCurrentPeriodGrantAsync`, an indexed `EXISTS` query
      against the migration-012 `uq_ai_credit_ledger_period` partial unique index on
      `(tenant_id, period_key, entry_type) WHERE period_key IS NOT NULL` — the SAME arbiter
      `PostGrantAsync`'s `ON CONFLICT DO NOTHING` resolves against, so the two can never disagree.

## 2. Implementation

- [x] 2.1 Inline lazy mint on grant-miss (reuse `PostGrantAsync`; no write on steady-state reads)

      Added `CreditGrantLazyMinter` (`Verbara.Platform.Billing`): `EnsureCurrentPeriodGrantAsync(TenantQuota?, ct)`
      — no-op when the quota or `AiCreditsMonthly` is null; otherwise an indexed `HasCurrentPeriodGrantAsync`
      check, and only on a miss, `PostGrantAsync` with the exact same entry shape
      `CreditGrantMintWorker.ProcessMintCycleAsync` posts (idempotent on `(tenant_id, period_key, entry_type)`).
      Wired into `DefaultQuotaEnforcementService.CheckAiCreditQuotaLedgerAsync` (constructed inline from the
      existing `ledger`/`clock` fields — no constructor signature change, so the 5 existing test call-sites
      are untouched) and into `CreditLedgerEndpoints.GetBalance` / `GetRemainingBySource` (DI-resolved
      singleton, registered in `AddPlatformBilling`). `CreditGrantMintWorker`'s doc-comment updated to record
      the fast-follow SHIPPED, citing this change id.

- [x] 2.2 InMemory store mirror

      `InMemoryCreditLedgerStore.HasCurrentPeriodGrantAsync` mirrors the Postgres existence check via the
      SAME `GrantKeys` idempotency set `PostGrantAsync` consults (`period:{entryType}:{periodKey}`), so the
      two paths can never disagree — identical semantics to the Postgres partial unique index.

## 3. Verification

- [x] 3.1 Deterministic rollover test (FakeTimeProvider across the month boundary)

      `CreditGrantLazyMinterTests` (Billing.Tests) — deterministic via a fixed `IClock` mock (no
      Task.Delay/wall-clock, per the test-determinism fences): a June grant, then a July `IClock` fixed
      just after the UTC rollover with the worker not yet ticked, asserts the July grant is lazy-minted
      inline and observed on that same first read.

- [x] 3.2 Concurrent first-read test (exactly one grant + single projection credit)

      `CreditGrantLazyMinterTests.EnsureCurrentPeriodGrantAsync_ShouldMintExactlyOnce_WhenConcurrentFirstReads`
      (InMemory, `Task.WhenAll`) + live-DB Postgres
      `PostGrantAsync_ShouldMintExactlyOnce_WhenConcurrentLazyMintAttemptsRaceSamePeriod` (Testcontainers,
      real `ON CONFLICT DO NOTHING` race) — both assert exactly one grant row / one projection credit, and
      a subsequent worker-shaped re-post is a no-op (idempotent).

- [x] 3.3 Live-DB Postgres ON CONFLICT coverage; `dotnet test` + CI green, zero warnings

      3 new `HasCurrentPeriodGrantAsync` Postgres tests (miss / different-period / hit) + the concurrent-race
      test above, all green against Testcontainers `postgres:16-alpine`. Full local `dotnet build` = 0
      warnings / 0 errors. `dotnet test` locally green: Billing.Tests 116/116, Storage.InMemory.Tests
      273/273, Storage.Postgres.Tests credit-ledger subset 40/40 (full-project run showed unrelated
      Testcontainers flakiness in `AuditEntriesNormalizationTests`/`PostgresTenantIpAllowlistStoreTests`/
      `ConversationVoiceLink` under many-fixture parallelism — confirmed pre-existing via a pristine
      `origin/main` worktree and by re-running each flaked fixture in isolation, where it passed; the
      credit-ledger fixture itself never failed across repeated full-suite runs), Api.Tests 1502/1502.
      CI-green is NOT checked here — that box is for CI, not local verification.
