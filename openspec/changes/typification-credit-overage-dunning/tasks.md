## 1. Phase A — Foundation (batch)

- [ ] 1.1 Add `OverageGraceDays` property (default `3`) to `DunningConfig` and verify it binds from the `Dunning` configuration section in `appsettings.json`
- [ ] 1.2 Add `IOverageNotificationService` interface with `NotifyThresholdCrossedAsync(TenantId, int threshold, long currentUsage, CancellationToken)` and `DefaultOverageNotificationService` stub implementation
- [ ] 1.3 Register `IOverageNotificationService` in `ServiceCollectionExtensions` (singleton, AOT-safe — no reflection)
- [ ] 1.4 Add `OverageThresholdState` Postgres table (or Redis key schema) for idempotency tracking of per-tenant per-period per-threshold notifications

## 2. Phase B — Overage Computation (focused)

- [ ] 2.1 Extend `DefaultInvoiceGenerationService.BuildInvoice` to read `TenantQuota.AiCreditsMonthly` and compute `OverageQuantity = max(0, totalAiAnalysis − AiCreditsMonthly)` for the `AiAnalysis` rate entry; set `IncludedQuantity` correctly
- [ ] 2.2 Add unit tests for `DefaultInvoiceGenerationService`: `GenerateAsync_ShouldIncludeOverageLineItem_WhenAiUsageExceedsMonthlyAllowance`, `GenerateAsync_ShouldNotIncludeOverage_WhenAiUsageBelowAllowance`, `GenerateAsync_ShouldBillFullQuantity_WhenAiCreditsMonthlyIsNull`
- [ ] 2.3 Implement `DefaultOverageNotificationService.NotifyThresholdCrossedAsync`: check idempotency store, dispatch via `INotificationService` to operator and tenant billing contacts, persist threshold-crossed record

## 3. Phase C — Grace-Period Issuance Worker (batch)

- [ ] 3.1 Implement `OverageInvoiceIssuanceWorker : BackgroundService` that runs on the `DunningConfig.CheckIntervalHours` cadence; queries `IInvoiceStore` for `InvoiceStatus.Draft` invoices whose `PeriodEnd + OverageGraceDays ≤ now`, transitions them to `InvoiceStatus.Issued`, sets `IssuedAt` and `DueDate`
- [ ] 3.2 Register `OverageInvoiceIssuanceWorker` as a hosted service in `ServiceCollectionExtensions`
- [ ] 3.3 Add unit tests: `ExecuteAsync_ShouldIssueOverageDraftInvoice_WhenGracePeriodElapsed`, `ExecuteAsync_ShouldNotIssueInvoice_WhenGracePeriodNotElapsed`

## 4. Phase C — Threshold Notification Wiring (batch)

- [ ] 4.1 Wire `IOverageNotificationService.NotifyThresholdCrossedAsync` into `DefaultMeteringService` (or the `AiAnalysis` metering code path): after recording each `AiAnalysis` usage record, compute running total and call notification service if 80 % or 100 % threshold is newly crossed
- [ ] 4.2 Add unit tests: `MeterAsync_ShouldNotifyAt80Percent_WhenThresholdFirstCrossed`, `MeterAsync_ShouldNotNotifyTwice_WhenThresholdAlreadyNotified`, `MeterAsync_ShouldNotifyAt100Percent_WhenOverageBegins`, `MeterAsync_ShouldNotNotify_WhenAiCreditsMonthlyIsNull`

## 5. Verification

- [ ] 5.1 Run `dotnet build Verbara.Platform.slnx` — zero warnings (`TreatWarningsAsErrors=true`)
- [ ] 5.2 Run `dotnet test Verbara.Platform.slnx` — all tests green, including new overage invoice + dunning trigger tests
- [ ] 5.3 Confirm `OverageInvoiceIssuanceWorker` is registered and starts cleanly via `dotnet run` in `src/Verbara.Platform.Api`
- [ ] 5.4 Verify AOT compatibility: no `IL2026`/`IL3050` diagnostics; all new DTOs registered in `[JsonSerializable]` contexts (`ApiJsonContext`)
- [ ] 5.5 CI green on PR — coverage ratchet, AOT publish gate, zero-warning gate
