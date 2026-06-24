## 1. Configuration — Extend PlatformLlmOptions

- [ ] 1.1 Add `InputCreditTokenRatio` (`long?`) and `OutputCreditTokenRatio` (`long?`) properties to `PlatformLlmOptions` in `src/Verbara.Platform.Llm/PlatformLlmOptions.cs`
- [ ] 1.2 Update XML doc on `CreditTokenRatio` to note it is the flat-fallback when per-direction ratios are absent

## 2. Credit Aggregation — Quota Enforcement

- [ ] 2.1 Introduce a helper (private method or internal record) in `DefaultQuotaEnforcementService` that computes the per-record credit contribution: parse `inputTokens`/`outputTokens` from `UsageRecord.Metadata` when per-direction ratios are set, fall back to flat `CreditTokenRatio` on `Quantity` when metadata keys are absent
- [ ] 2.2 Replace the current aggregate `TotalQuantity / _creditTokenRatio` division in `GetLimitForType` (or the quota check path) with a record-level differentiated computation, accepting that `IUsageRecordStore` may need to expose raw records (not just summaries) for the `AiAnalysis` type — or implement a dedicated credit-sum query path
- [ ] 2.3 Verify that the fallback branch (records without `inputTokens`/`outputTokens` metadata) produces results identical to the current flat-ratio behavior

## 3. Invoice Generation — Line-Item Description

- [ ] 3.1 In `DefaultInvoiceGenerationService.CalculateFlatLineItem` (or the `AiAnalysis`-specific path), inject or accept the active ratio configuration so the description can reflect `"AI Analysis (input/output pricing)"` when per-direction ratios are set
- [ ] 3.2 Ensure the `InvoiceLineItem` type (sealed record) and `ApiJsonContext` registration are unchanged — description is already a `string` property; no new DTO is required

## 4. Tests

- [ ] 4.1 `AggregateCredits_ShouldUsePerDirectionRatios_WhenMetadataPresent` — mixed batch of records: some with `inputTokens`/`outputTokens` metadata and per-direction ratios, assert credit total equals sum of per-direction contributions
- [ ] 4.2 `AggregateCredits_ShouldFallbackToFlatRatio_WhenMetadataAbsent` — records without metadata keys, assert credit total equals `Quantity / CreditTokenRatio` (matches current behavior)
- [ ] 4.3 `AggregateCredits_ShouldHandleMixedRecords_WithoutCrossContamination` — one record with metadata, one without, in the same period; assert each uses its own path independently
- [ ] 4.4 `GenerateInvoice_ShouldDescribePerDirectionPricing_WhenRatiosConfigured` — assert `InvoiceLineItem.Description` contains differentiated-pricing indicator when both per-direction ratios are set
- [ ] 4.5 `GenerateInvoice_ShouldUseStandardDescription_WhenFlatRatioOnly` — assert `InvoiceLineItem.Description` is unchanged from current behavior when per-direction ratios are absent
- [ ] 4.6 `CheckQuota_ShouldReflectDifferentiatedCreditTotal_WhenRatiosConfigured` — quota exhaustion check uses differentiated credit sum, not flat total

## 5. Verification

- [ ] 5.1 `dotnet build Verbara.Platform.slnx` — zero warnings (TreatWarningsAsErrors=true, WarningLevel=9999)
- [ ] 5.2 `dotnet test Verbara.Platform.slnx` — all tests green including new tests from Task 4
- [ ] 5.3 Confirm AOT compatibility: no `IL2026`/`IL3050` diagnostics introduced; all new logic is reflection-free string parsing and arithmetic
- [ ] 5.4 CI green on the feature branch
