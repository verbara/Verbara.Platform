## 1. Phase A — Domain + period helper (batch)

- [ ] 1.1 Add `CreditLedgerEntry` (sealed class, `{ get; init; }`): `EntryId` (EntityId), `TenantId`, `EntryType` (enum Grant/Debit), `Source` (enum Subscription/TopUp/Promo/Partner/PostPaid), `Amount` (decimal, signed), `PeriodKey` (string?), `ExternalRef` (string?), `ExpiresAt` (DateTimeOffset?), `UsageRecordId` (string?), `CreatedAt`. Hand-written `static Map(NpgsqlDataReader)`. In `Verbara.Platform.Billing`.
- [ ] 1.2 Add `CreditDebitResult` discriminated outcome (`Posted` with new balance / `RejectedInsufficientBalance`) — sealed record or readonly struct, AOT-safe.
- [ ] 1.3 Add `BillingPeriod` static helper: `Current(IClock)` → `(DateTimeOffset Start, DateTimeOffset End, string Key)` where `Start = new DateTimeOffset(now.Year, now.Month, 1,0,0,0, TimeSpan.Zero)`, `End = Start.AddMonths(1)`, `Key = $"{now.Year:D4}-{now.Month:D2}"`. In `Verbara.Platform.Billing`.
- [ ] 1.4 Define `ICreditLedgerStore`: `Task<decimal> GetBalanceAsync(TenantId, ct)`; `Task PostGrantAsync(CreditLedgerEntry grant, ct)` (idempotent via period_key/external_ref); `Task<CreditDebitResult> TryPostDebitAsync(TenantId, decimal amount, string? usageRecordId, ct)`; `Task<IReadOnlyList<CreditLedgerEntry>> GetEntriesAsync(TenantId, int page, int pageSize, ct)`.

## 2. Phase A — Period-helper refactor (batch, behaviour-preserving)

- [ ] 2.1 Replace the inlined `GetCurrentPeriod()` in `DefaultQuotaEnforcementService`, `BillingTypificationCreditMeter`, `DefaultMeteringService`, and `AiCreditsEndpoints` with `BillingPeriod.Current(clock)` (and the `"yyyy-MM"` key where useful). No boundary change.
- [ ] 2.2 Characterization tests: pin current `DefaultQuotaEnforcementService.CheckQuotaAsync` (AiAnalysis, per-direction + flat) and `DefaultInvoiceGenerationService.BuildAiCreditLineItemAsync` outputs byte-for-byte for representative inputs (these guard change (b)'s cutover). Add a `BillingPeriod` boundary test matching the pre-refactor values.

## 3. Phase B — Migration + Postgres store (focused)

- [ ] 3.1 Migration `012_credit_ledger.sql` (EmbeddedResource, idempotent `IF NOT EXISTS`): `ai_credit_ledger` (entry_id TEXT PK, tenant_id TEXT NOT NULL, entry_type SMALLINT NOT NULL, source SMALLINT NOT NULL, amount NUMERIC(18,6) NOT NULL, period_key TEXT NULL, external_ref TEXT NULL, expires_at TIMESTAMPTZ NULL, usage_record_id TEXT NULL, created_at TIMESTAMPTZ NOT NULL); `tenant_credit_balance` (tenant_id TEXT PK, balance NUMERIC(18,6) NOT NULL DEFAULT 0, version BIGINT NOT NULL DEFAULT 0, updated_at TIMESTAMPTZ NOT NULL); index `(tenant_id, created_at)`; partial unique `(tenant_id, period_key, entry_type) WHERE period_key IS NOT NULL`; partial unique `(tenant_id, external_ref) WHERE external_ref IS NOT NULL`.
- [ ] 3.2 `PostgresCreditLedgerStore` (internal sealed): `GetBalanceAsync` = O(1) `SELECT balance FROM tenant_credit_balance WHERE tenant_id=@t` (0 when absent). `PostGrantAsync` = one tx: `INSERT … ON CONFLICT DO NOTHING` into ledger; if inserted (rows=1) upsert projection `INSERT … ON CONFLICT (tenant_id) DO UPDATE SET balance = balance + @amount, version = version + 1`. `TryPostDebitAsync` = one tx: guarded `UPDATE tenant_credit_balance SET balance = balance - @debit, version = version + 1 WHERE tenant_id=@t AND balance >= @debit`; if rows=1 INSERT the debit ledger row and return `Posted(newBalance)`, else rollback and return `RejectedInsufficientBalance`. Explicit `NpgsqlDbType` on all nullable params; use the `NpgsqlExecutor` connection+transaction overload.

## 4. Phase C — InMemory twin + DI (batch)

- [ ] 4.1 `InMemoryCreditLedgerStore` (internal sealed): `ConcurrentDictionary` of entries + a per-tenant balance/version under a lock; compare-and-decrement for `TryPostDebitAsync` (mirror Postgres semantics incl. idempotency on period_key/external_ref); for dev/test default parity.
- [ ] 4.2 Register `AddSingleton<ICreditLedgerStore, PostgresCreditLedgerStore>()` in Storage.Postgres SCE Billing block and `AddSingleton<ICreditLedgerStore, InMemoryCreditLedgerStore>()` in Storage.InMemory SCE Billing block. No hosted service, endpoint, or permission in this change.

## 5. Tests + verification

- [ ] 5.1 `CreditLedger`/store unit tests (both twins): `PostGrantAsync_ShouldIncreaseBalance_WhenApplied`, `PostGrantAsync_ShouldBeNoOp_WhenDuplicatePeriodKey`, `TryPostDebitAsync_ShouldPost_WhenBalanceSufficient`, `TryPostDebitAsync_ShouldReject_WhenBalanceInsufficient`, `TryPostDebitAsync_ShouldNeverGoNegative_UnderConcurrentDebits`, `GetBalanceAsync_ShouldReturnZero_WhenNoLedger`.
- [ ] 5.2 Postgres round-trip tests (Testcontainers): ledger SUM equals projection balance after a grant+debit sequence; idempotent grant; concurrent-debit race (note: CI does not run Testcontainers — verify locally).
- [ ] 5.3 Characterization tests green (task 2.2) — proving the period-helper refactor changed no number.
- [ ] 5.4 `dotnet build Verbara.Platform.slnx` — zero warnings.
- [ ] 5.5 `dotnet test Verbara.Platform.slnx` — all green; AOT publish gate clean (no IL2026/IL3050/IL207x; any DTO in `[JsonSerializable]` — none expected this change).
- [ ] 5.6 Confirm inertness: no enforcement/metering/invoice/API path reads or writes the ledger (grep `ICreditLedgerStore` consumers = stores + tests only).
