# Tasks — credit-ledger-cutover (change b, Model C / ADR-0033 addendum 2026-06-27)

> Re-grounded against post-(a) code. FCM batching: Phase A foundation (batch), Phase B critical
> (one focused subagent per task), Phase C integration (batch). Native AOT, `TreatWarningsAsErrors`,
> test naming `Method_ShouldExpected_WhenCondition`. Two feature flags, default **off**:
> `LedgerEnforcementEnabled` (quota+meter) and `LedgerInvoiceReadEnabled` (invoice). Both added to the
> already-injected `PlatformLlmOptions` (zero new DI fan-out) and bound from `Llm:Platform:*` in `Program.cs`.

## Phase A — Foundation (batch)

- [ ] A1. `QuotaOutcome { Allow, Warn, SoftBlock, HardBlock }` enum (`Verbara.Platform.Billing/TenantQuota.cs`
  or a new file); add `QuotaOutcome Outcome = QuotaOutcome.Allow` as the **4th positional** member of
  `QuotaCheckResult` (`TenantQuota.cs:35`) — default keeps all existing 3-arg constructions compiling.
- [ ] A2. Add `bool LedgerEnforcementEnabled` and `bool LedgerInvoiceReadEnabled` (default false) to
  `PlatformLlmOptions` (`Verbara.Platform.Llm/PlatformLlmOptions.cs`) with crisp XML docs (cutover kill
  switches; flip order enforcement→shadow→invoice). Bind both in `Program.cs:201-222` `configurePlatform`
  from `Llm:Platform:LedgerEnforcementEnabled` / `…LedgerInvoiceReadEnabled` (reflection-free `bool.TryParse`).
- [ ] A3. `ICreditLedgerStore` (`Verbara.Platform.Billing/ICreditLedgerStore.cs`): add
  `Task<MeteredDebitResult> PostMeteredDebitAsync(TenantId tenantId, decimal debit, CreditSource coveredSource, string? usageRecordId, CancellationToken ct);`
  and `Task<decimal> GetPostPaidDebitsTotalAsync(TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct);`
  Add `MeteredDebitResult` (AOT-safe `readonly record struct` with `decimal NewBalance, CoveredAmount, PostPaidAmount`).
  Parameterise `TryPostDebitAsync` to take a `CreditSource source` (covered draws record their lot; stop
  hard-coding `PostPaid`) — update its XML doc + the inert-substrate note.

## Phase B — Critical (one focused subagent each)

- [ ] B1. **Postgres `PostMeteredDebitAsync`** (`Storage.Postgres/Stores/PostgresCreditLedgerStore.cs`):
  one `NpgsqlTransaction` — `SELECT balance … FOR UPDATE` (absent ⇒ 0); `covered = Min(balance, debit)`,
  `tail = debit − covered`; if `covered > 0` guarded `UPDATE … WHERE balance >= @covered` + INSERT `−covered`
  debit `source=@coveredSource`; if `tail > 0` INSERT `−tail` debit `source=PostPaid` (no projection write);
  commit; return `MeteredDebitResult`. Fix the parameterised `TryPostDebitAsync` source. Implement
  `GetPostPaidDebitsTotalAsync` (`SELECT COALESCE(-SUM(amount),0) FROM ai_credit_ledger WHERE tenant_id=@t AND
  source=@PostPaid AND entry_type=@Debit AND created_at >= @start AND created_at < @end`). Explicit
  `NpgsqlDbType` on every param. Tests in `Storage.Postgres.Tests` (covered, overflow, concurrency, Σ-PostPaid).
- [ ] B2. **InMemory `PostMeteredDebitAsync`** (`Storage.InMemory/InMemoryCreditLedgerStore.cs`): identical
  covered/tail split under the existing per-tenant `lock(ledger.Gate)`; parameterised `TryPostDebitAsync`
  source; `GetPostPaidDebitsTotalAsync` over the in-memory entries. Behaviour-twin tests.
- [ ] B3. **Quota cutover** (`Verbara.Platform.Billing/DefaultQuotaEnforcementService.cs`): inject
  `ICreditLedgerStore`. When `LedgerEnforcementEnabled`, the AiAnalysis path reads `GetBalanceAsync`; map:
  null `AiCreditsMonthly` ⇒ `Allow` (unlimited, no ledger read); `balance >= projectedDebit` ⇒ `Allow`;
  else map `QuotaAction` → `Outcome` (**`Warn` ⇒ `Warn`/Allowed=true** (overflow), `SoftBlock` ⇒ `SoftBlock`,
  `HardBlock` ⇒ `HardBlock`). Set `Outcome` in the **legacy path too** (so the endpoint switch is
  flag-independent). Boundary `>=`. Reason credit-denominated under the flag. Singleton-safe.
- [ ] B4. **Metering cutover** (`Api/Services/BillingTypificationCreditMeter.cs`): when
  `LedgerEnforcementEnabled`, after `RecordBatchAsync` (audit) post `PostMeteredDebitAsync(tenant,
  CreditsForRecord(...), CreditSource.Subscription, record.RecordId.Value, ct)` in its own best-effort
  try/catch (NEVER breaks metering; a rejected/failed debit is logged via a source-gen `[LoggerMessage]`).
  Threshold straddle unchanged. Keep the meter Singleton-safe.
- [ ] B5. **Invoicing cutover** (`Verbara.Platform.Billing/DefaultInvoiceGenerationService.cs`): inject
  `ICreditLedgerStore`. When `LedgerInvoiceReadEnabled`, `BuildAiCreditLineItemAsync` derives
  `overage = GetPostPaidDebitsTotalAsync(period)`, `consumedCredits = allowance + overage` (display), keeping
  the line-item shape (`Quantity`, `IncludedQuantity = allowance`, `OverageQuantity = overage`,
  `Amount = overage × UnitPrice`, `DescribeRate`). Flag off ⇒ unchanged `usage_records` path.
- [ ] B6. **Subscription mint worker** (`Verbara.Platform.Billing/CreditGrantMintWorker.cs`): `BackgroundService`
  mirroring `OverageInvoiceIssuanceWorker` (const `ResiliencePolicyKey`, `IServiceScopeFactory`,
  `IOptions<DunningConfig>` cadence, keyed `ResiliencePolicy`, `internal ProcessMintCycleAsync`). Each cycle:
  for every tenant with non-null `AiCreditsMonthly`, `PostGrantAsync` a `Subscription` grant for
  `BillingPeriod.Current(clock).Key`, amount = allowance, `expires_at = periodEnd` (idempotent). Needs a
  tenant enumeration source — ground the available store (e.g. `ITenantQuotaStore` listing or the tenant
  catalog) before coding.
- [ ] B7. **Back-fill migration 013** (`Storage.Postgres/Migrations/013_credit_ledger_backfill.sql`): for each
  tenant with non-null `ai_credits_monthly`, INSERT the current-period `Subscription` grant (`ON CONFLICT DO
  NOTHING`), a covered debit, and — if reconstructed consumed > allowance — a `PostPaid` debit with
  `external_ref = 'backfill:<period>'`, then upsert the projection. **`consumedSoFar` reconstructed from
  `usage_records` on the frozen ratio basis.** Idempotent (`IF NOT EXISTS` / `ON CONFLICT`). Verify ordering
  vs the mint worker (back-fill owns the seed; mint `ON CONFLICT` makes overlap safe).

## Phase C — Integration (batch)

- [ ] C1. **Endpoint Outcome switch** (`Api/Endpoints/ConversationEndpoints.cs:324-335`): replace the
  `!q.Allowed` + second `GetQuotaStatusAsync` block with a switch on `q.Outcome` (`HardBlock` ⇒ 402 typed
  `ErrorResponse`; `SoftBlock` ⇒ `EmptySuggestion`; `Warn`/`Allow` ⇒ proceed). Keep the ADR-0032 entitlement
  re-check **before** the quota gate (order is load-bearing). Drop the now-unused second read.
- [ ] C2. **Program.cs wiring**: register `CreditGrantMintWorker` (`AddHostedService`) + its keyed
  `ResiliencePolicy` (mirror the dunning hourly-policy block ~line 899-905); confirm both flags bound (A2).
- [ ] C3. **Re-seed characterization tests** (`tests/Verbara.Platform.Billing.Tests/CreditLedgerCharacterizationTests.cs`):
  add ledger-seeded variants (Subscription grant + covered/PostPaid debits to the same consumed values) that
  assert the **same** outcomes with the enforcement/invoice flags **on**; update the flat-path `Reason` to its
  credit-denominated form (record the change — Reason is internal-only). Keep the original flag-off tests green
  unchanged. Add Warn-overflow, HardBlock-402, and Σ-PostPaid invoice cases.
- [ ] C4. **Verification**: `dotnet build` 0 warnings; full suite green (Billing + Api + InMemory; Postgres
  Testcontainers locally — CI skips live DB); AOT publish gate clean; `openspec validate credit-ledger-cutover
  --strict`. Confirm flag-off path byte-identical and flag-on path matches the characterization values.
