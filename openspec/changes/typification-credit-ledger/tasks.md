## 1. Phase A — Foundation (batch)

- [ ] 1.1 Define `CreditLedgerEntry` (sealed class, `entry_type` discriminator: Grant/Debit, `amount_credits`, `reason`, `reference_record_id`, `correlation_id`, `external_transaction_id`, `created_at`) in `Verbara.Platform.Billing`
- [ ] 1.2 Define `CreditLedger` aggregate (balance computation from entries, `AddGrant`, `AddDebit` — throws `QuotaExceededException` on underflow) in `Verbara.Platform.Billing`
- [ ] 1.3 Define `ICreditLedgerStore` interface (GetBalanceAsync, GetEntriesAsync paginated, AddEntryAsync, FindByExternalTransactionIdAsync) in `Verbara.Platform.Billing`
- [ ] 1.4 Define `ICreditLedgerService` interface (TopUpAsync, PostDebitAsync, GetBalanceAsync, GetEntriesAsync) in `Verbara.Platform.Billing`
- [ ] 1.5 Author Postgres migration: `credit_ledger` table + partial unique index on `(tenant_id, external_transaction_id) WHERE external_transaction_id IS NOT NULL`; migration must be idempotent (IF NOT EXISTS guards)
- [ ] 1.6 Add `AiCreditLedger` to `UsageType` enum if a distinct AI-credit usage type is needed (confirm against existing `AiAnalysis` — reuse or add)

## 2. Phase B — Critical Components (focused subagents)

- [ ] 2.1 Implement `DefaultCreditLedgerStore` in `Storage.Postgres` using `NpgsqlExecutor` + hand-written `Map(NpgsqlDataReader)` (NO Dapper); all nullable params with explicit `NpgsqlDbType`
- [ ] 2.2 Implement `DefaultCreditLedgerService`: `TopUpAsync` (idempotency via `FindByExternalTransactionIdAsync`; DB unique-constraint as safety net), `PostDebitAsync` (atomic `UsageRecord + Debit` in single Npgsql transaction)
- [ ] 2.3 Update `DefaultQuotaEnforcementService.CheckQuotaAsync`: when ledger has entries for tenant → use ledger balance for `UsageType.AiAnalysis`; fall through to monthly-allowance when no ledger
- [ ] 2.4 Update `DefaultMeteringService.RecordUsageAsync`: for `UsageType.AiAnalysis` + tenant-with-ledger → call `PostDebitAsync` inside the same Npgsql transaction; propagate `QuotaExceededException` to caller
- [ ] 2.5 Update `IInvoiceGenerationService` implementation: subtract prepaid debit total from AI usage before calculating chargeable amount; add credit-consumption summary line to invoice model
- [ ] 2.6 Define request/response DTOs (`TopUpRequest`, `CreditLedgerBalanceResponse`, `CreditLedgerEntryResponse`, `CreditLedgerEntriesPage`) as sealed records; register all in `ApiJsonContext` with `[JsonSerializable]`

## 3. Phase C — Integration (batch)

- [ ] 3.1 Register `ICreditLedgerStore` / `ICreditLedgerService` / `DefaultCreditLedgerStore` / `DefaultCreditLedgerService` in `ServiceCollectionExtensions` (Billing package DI extension + Storage.Postgres extension)
- [ ] 3.2 Add endpoint group `BillingCreditLedgerEndpoints`: `POST /api/v1/billing/credit-ledger/top-up` (permission `billing:credit-ledger:write`), `GET /api/v1/billing/credit-ledger/balance` (permission `billing:credit-ledger:read`), `GET /api/v1/billing/credit-ledger/entries` (permission `billing:credit-ledger:read`, paginated)
- [ ] 3.3 Add `billing:credit-ledger:read` + `billing:credit-ledger:write` permissions to `RoleTemplateSeeder` (Admin + Platform Admin get write; Supervisor + above get read)
- [ ] 3.4 Map `BillingCreditLedgerEndpoints` in `Program.cs`

## 4. Web UI (Platform.Web repo)

- [ ] 4.1 Add `useCreditLedgerBalance` TanStack Query hook (`GET /api/v1/billing/credit-ledger/balance`)
- [ ] 4.2 Add `useCreditLedgerEntries` hook with pagination support
- [ ] 4.3 Build `CreditLedgerBalanceWidget` component: displays balance, totalGranted, totalDebited; zero-state CTA; error boundary for network failures
- [ ] 4.4 Integrate widget into billing settings panel (existing billing page route)
- [ ] 4.5 Add i18n strings for balance widget and top-up flow in EN-US, ES-419, PT-BR (i18n parity CI gate)

## 5. Tests + AOT Verification

- [ ] 5.1 Unit tests for `CreditLedger` aggregate: `AddGrant_ShouldIncreaseBalance_WhenAmountIsPositive`, `AddDebit_ShouldDecreaseBalance_WhenSufficientBalance`, `AddDebit_ShouldThrow_WhenBalanceInsufficient`
- [ ] 5.2 Unit tests for `DefaultCreditLedgerService`: idempotency on duplicate `externalTransactionId`, atomic debit rollback on underflow
- [ ] 5.3 Unit tests for updated `DefaultQuotaEnforcementService`: ledger takes precedence over monthly allowance, monthly allowance used when no ledger
- [ ] 5.4 Unit tests for updated `DefaultMeteringService`: debit posted for AI analysis when ledger exists, no debit posted when no ledger
- [ ] 5.5 Integration test for top-up endpoint (201 on success, 200 on idempotent replay, 403 on missing permission)
- [ ] 5.6 Integration test for balance endpoint (correct balance after grant + debit sequence)
- [ ] 5.7 `dotnet test Verbara.Platform.slnx` — all tests green, zero warnings (`TreatWarningsAsErrors`)
- [ ] 5.8 `dotnet publish src/Verbara.Platform.Api -r linux-x64 -c Release` — zero `IL2026`/`IL3050`/`IL207x` diagnostics (AOT gate)
- [ ] 5.9 CI green on PR (coverage ratchet, AOT gate, i18n parity)
