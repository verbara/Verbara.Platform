## 1. Phase A — Foundation (batch)

- [ ] 1.1 Add `OverageGraceDays` (default `3`) and `PaymentTermDays` (default `14`) to `DunningConfig` (`src/Verbara.Platform.Billing/DunningConfig.cs`); clamp negatives to `0` at read sites.
- [ ] 1.2 Reuse the already-registered `billing.quota_warning` (Warning · `admin`,`system_admin`) and `billing.quota_exceeded` (Critical · `admin`,`system_admin`,`platform_admin`) types in `NotificationTypeRegistry.cs` — confirmed registered-but-never-emitted (no `CreateAsync` producer), so no registry edit and no new type are needed.
- [ ] 1.3 Add migration `011_invoice_due_date_payment_status.sql` adding `due_date timestamptz NULL` and `payment_status smallint NOT NULL DEFAULT 0` to the `invoices` table (`src/Verbara.Platform.Storage.Postgres/Migrations/`); register it as `<EmbeddedResource>` following `010_platform_llm_credits.sql`.
- [ ] 1.4 Extend `PostgresInvoiceStore` (`src/Verbara.Platform.Storage.Postgres/Stores/PostgresInvoiceStore.cs`): add `due_date`/`payment_status` to INSERT, SELECT (all three queries), the row record, `Map`, and `ToInvoice`. `SaveAsync` is INSERT-only today → convert to upsert (`ON CONFLICT (invoice_id) DO UPDATE SET status, payment_status, issued_at, paid_at, due_date, line_items, …`) so a dunning `PaymentStatus` mutation + re-`SaveAsync` round-trips instead of throwing a duplicate-key error. Bind nullable `due_date` with explicit `NpgsqlDbType.TimestampTz` (mirror `IssuedAt`/`PaidAt`).
- [ ] 1.5 Confirm `InMemoryInvoiceStore` already round-trips `DueDate`/`PaymentStatus` (it stores the whole object) — add a guard test if not covered.

## 2. Phase B — Allowance-based overage line item (focused)

- [ ] 2.1 Add an `ITenantQuotaStore` ctor dependency to `DefaultInvoiceGenerationService` (`src/Verbara.Platform.Billing/DefaultInvoiceGenerationService.cs`); update all existing `Build()` test helpers + the `AddPlatformBilling` registration (the store is already DI-registered).
- [ ] 2.2 In `BuildInvoice` (or a new private `CalculateAiCreditOverageLineItem`), branch `AiAnalysis` to the allowance basis: read `TenantQuota.AiCreditsMonthly`; compute `consumedCredits` mirroring `DefaultQuotaEnforcementService` (per-direction via `GetAiTokenBreakdownAsync` when both `PlatformLlmOptions` ratios `> 0`, else flat `tokens / CreditTokenRatio`); set `IncludedQuantity = AiCreditsMonthly` (or `0` when null), `OverageQuantity = max(0, consumedCredits − allowance)`, `Quantity = consumedCredits`, `UnitPrice` from the `AiAnalysis` `RateEntry`, `Amount = OverageQuantity × UnitPrice`. Skip when no `AiAnalysis` `RateEntry` exists.
- [ ] 2.3 Tests (`tests/Verbara.Platform.Billing.Tests/DefaultInvoiceGenerationServiceTests.cs`): `GenerateAsync_ShouldEmitAllowanceBasedOverage_WhenAiCreditsExceedMonthlyAllowance`, `GenerateAsync_ShouldEmitZeroOverage_WhenAiCreditsBelowAllowance`, `GenerateAsync_ShouldBillFullConsumption_WhenAiCreditsMonthlyIsNull`, `GenerateAsync_ShouldUsePerDirectionCredits_WhenBothRatiosSet`, `GenerateAsync_ShouldSkipAiAnalysisLine_WhenNoRateEntry`. Substitute `ITenantQuotaStore`/`IUsageRecordStore`; fixed `Substitute.For<IClock>()`.

## 3. Phase B — Threshold-notification hook (focused)

- [ ] 3.1 Inject `INotificationService` (from `Verbara.Platform.Core.Notifications`) and a credit-usage source into `BillingTypificationCreditMeter` (`src/Verbara.Platform.Api/Services/BillingTypificationCreditMeter.cs`); keep it singleton-safe (it is `AddSingleton` at `Program.cs:237`).
- [ ] 3.2 After the existing `RecordBatchAsync`, when the tenant `AiCreditsMonthly` is non-null, compute `currentCredits` (post-record period total, same basis as quota enforcement) and `previousCredits = currentCredits − thisRecordCredits`; dispatch `billing.quota_warning` when `previousCredits < 0.8×allowance ≤ currentCredits` and `billing.quota_exceeded` when `previousCredits < allowance ≤ currentCredits`. No-op when `AiCreditsMonthly` is null. Wrap dispatch so a notification failure never breaks metering.
- [ ] 3.3 Tests (`tests/Verbara.Platform.Api.Tests/…` — has Storage.InMemory IVT): `RecordAsync_ShouldNotifyWarning_WhenCrossing80PercentFirstTime`, `RecordAsync_ShouldNotNotifyWarning_WhenAlreadyAbove80Percent`, `RecordAsync_ShouldNotifyExceeded_WhenCrossing100Percent`, `RecordAsync_ShouldNotNotify_WhenAiCreditsMonthlyIsNull`. Assert via `Substitute.For<INotificationService>().Received(1).CreateAsync(…)`.

## 4. Phase B — Overage invoice issuance worker (focused)

- [ ] 4.1 Implement `OverageInvoiceIssuanceWorker : BackgroundService` (`src/Verbara.Platform.Billing/OverageInvoiceIssuanceWorker.cs`) mirroring `DunningService`: `public const string ResiliencePolicyKey`; ctor `(IServiceScopeFactory, ILogger<…>, IOptions<DunningConfig>, [FromKeyedServices(Key)] ResiliencePolicy? = null)`; `ExecuteAsync` loops on `CheckIntervalHours`; `internal async Task ProcessIssuanceCycleAsync(CancellationToken)` resolves `IInvoiceStore` + `IClock` per scope, lists `Draft` invoices with an `AiAnalysis` `OverageQuantity > 0` line whose `PeriodEnd + OverageGraceDays ≤ UtcNow`, sets `Status = Issued`, `IssuedAt = UtcNow`, `DueDate = UtcNow + PaymentTermDays`, persists via `SaveAsync`. `[LoggerMessage]` partials.
- [ ] 4.2 Tests (`tests/Verbara.Platform.Billing.Tests/OverageInvoiceIssuanceWorkerTests.cs`): `ProcessIssuanceCycle_ShouldIssueDraft_WhenGraceElapsedAndOverageLineExists`, `ProcessIssuanceCycle_ShouldNotIssue_WhenGraceNotElapsed`, `ProcessIssuanceCycle_ShouldNotIssue_WhenNoOverageLine`, `ProcessIssuanceCycle_ShouldSetDueDate_FromPaymentTermDays`. Drive `ProcessIssuanceCycleAsync` directly (InternalsVisibleTo); fixed clock; `Substitute.For<IInvoiceStore>()`; need `IInvoiceStore` to expose Draft listing (add `ListByStatusAsync(Draft)` use or a draft query).

## 5. Phase C — Integration (batch)

- [ ] 5.1 `Program.cs`: extend the manual `Configure<DunningConfig>` block (`~535-543`) with `int.TryParse` lines for `OverageGraceDays` and `PaymentTermDays`.
- [ ] 5.2 `Program.cs`: `AddHostedService<OverageInvoiceIssuanceWorker>()` and register its keyed `ResiliencePolicy` alongside the dunning one (`~896-899`).
- [ ] 5.3 `ManagementBillingEndpoints.IssueInvoice` (`src/Verbara.Platform.Api/Endpoints/ManagementBillingEndpoints.cs:249`): set `DueDate = clock.UtcNow + PaymentTermDays` when transitioning to `Issued` (so manually-issued invoices also enter dunning); inject `IClock` + `IOptions<DunningConfig>`. Add a dedicated store method if `UpdateStatusAsync` cannot carry `DueDate`.
- [ ] 5.4 Postgres round-trip test (`tests/Verbara.Platform.Storage.Postgres.Tests/Stores/PostgresInvoiceStoreTests.cs`, Testcontainers): `SaveAsync_ShouldPersistDueDateAndPaymentStatus_WhenRoundTripped`, `GetByIdAsync_ShouldRehydratePaymentStatus_AfterDunningMutation`. (CI does not run Testcontainers — verify locally.)
- [ ] 5.5 If a new HTTP response DTO is introduced, register it in `ApiJsonContext`; otherwise confirm the notification path serializes only the already-registered `NotificationEvent`.

## 6. Verification

- [ ] 6.1 `dotnet build Verbara.Platform.slnx` — zero warnings (`TreatWarningsAsErrors=true`).
- [ ] 6.2 `dotnet test Verbara.Platform.slnx` — all tests green (new overage line-item, threshold-notification, issuance-worker, and Postgres round-trip tests included).
- [ ] 6.3 Confirm `OverageInvoiceIssuanceWorker` registers and the host starts cleanly via `dotnet run` in `src/Verbara.Platform.Api`.
- [ ] 6.4 AOT gate: no `IL2026`/`IL3050`/`IL207x` diagnostics; any new serialized DTO is in `[JsonSerializable]`; all new DI is reflection-free (closed-generic).
- [ ] 6.5 CI green on PR — Build + Unit Tests, Analyze (C#), Coverage Ratchet, CodeQL, Dependency Review.
