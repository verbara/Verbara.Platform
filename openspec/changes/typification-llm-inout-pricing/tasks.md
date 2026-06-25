## 0. Grounding (done) — design locked

- [x] 0.1 Current AiAnalysis quota is token-vs-token off an AGGREGATE summary (`limit = AiCreditsMonthly × CreditTokenRatio` tokens; usage = `GetSummaryByTypeAsync(...).TotalQuantity`); the summary carries NO metadata.
- [x] 0.2 Per-direction split lives ONLY in `UsageRecord.Metadata` keys `inputTokens`/`outputTokens` (InvariantCulture int strings, written by `BillingTypificationCreditMeter` for every AiAnalysis record since P2c.2). `Metadata` is `Dictionary<string,string>?` (nullable, jsonb column `metadata`).
- [x] 0.3 Design: differentiated total DECOMPOSES → use ONE store aggregation `GetAiTokenBreakdownAsync` returning raw token sums `(InputTokens, OutputTokens, UnsplitTokens)`; the quota service applies ratios (credits basis). Invoice = description-only (amount stays rate-card/token-driven). Tests stub the new store method on the NSubstitute `IUsageRecordStore` mock.

## 1. Configuration — Extend PlatformLlmOptions

- [x] 1.1 Add `InputCreditTokenRatio` (`long?`) and `OutputCreditTokenRatio` (`long?`) to `src/Verbara.Platform.Llm/PlatformLlmOptions.cs`; XML-doc both + note `CreditTokenRatio` is the flat fallback.
- [x] 1.2 Bind the two new keys from `Llm:Platform` in `Program.cs` using the existing per-key `long.TryParse(..., InvariantCulture, ...)` pattern (only set when present, leaving null otherwise).

## 2. Store — Aggregated per-direction breakdown

- [x] 2.1 Add `AiTokenBreakdown(decimal InputTokens, decimal OutputTokens, decimal UnsplitTokens)` (sealed record, Billing namespace) + `Task<AiTokenBreakdown> GetAiTokenBreakdownAsync(TenantId tenantId, UsageType type, DateTimeOffset from, DateTimeOffset until, CancellationToken ct)` to `IUsageRecordStore`.
- [x] 2.2 Postgres impl (`PostgresUsageRecordStore`): single aggregation query over `usage_records` filtered by tenant/usage_type/recorded_at range. Use `jsonb_exists(metadata,'inputTokens')` (NOT the `?` operator). Split bucket = `metadata IS NOT NULL AND jsonb_exists(metadata,'inputTokens') AND jsonb_exists(metadata,'outputTokens')` → `SUM((metadata->>'inputTokens')::numeric)` / `outputTokens`; unsplit bucket = everything else (incl. `metadata IS NULL`) → `SUM(quantity)`. Wrap each `SUM` in `COALESCE(...,0)`. Read via a `static Map(NpgsqlDataReader)` row type with name-based `GetDecimal` getters.
- [x] 2.3 InMemory impl (`InMemoryUsageRecordStore`): mirror with LINQ — split = records where `Metadata` non-null and has both keys → parse `inputTokens`/`outputTokens` with `decimal.Parse(..., InvariantCulture)`; unsplit = the rest → sum `Quantity`.

## 3. Quota — Differentiated credit aggregation

- [x] 3.1 In `DefaultQuotaEnforcementService`: cache `_inputRatio`/`_outputRatio` (`long?`) from `PlatformLlmOptions` (alongside `_creditTokenRatio`); a private `bool PerDirectionActive => _inputRatio is > 0 && _outputRatio is > 0`.
- [x] 3.2 Add a differentiated branch at the TOP of the AiAnalysis path in `CheckQuotaAsync`: only when `type == AiAnalysis && PerDirectionActive`. Compute `currentCredits = bd.InputTokens/_inputRatio + bd.OutputTokens/_outputRatio + bd.UnsplitTokens/_creditTokenRatio` from `GetAiTokenBreakdownAsync`; `limitCredits = quota.AiCreditsMonthly` (null → unlimited/allowed); `additionalCredits = additionalQuantity / _creditTokenRatio`; `projected = currentCredits + additionalCredits`; `usagePercent = (double)(projected/limitCredits*100m)`; `projected <= limitCredits` → allowed, else apply `QuotaAction` (Warn allow / SoftBlock+HardBlock deny) mirroring the existing reason/switch.
- [x] 3.3 Leave the existing flat path (`GetLimitForType` + `GetSummaryByTypeAsync` token comparison) BYTE-IDENTICAL for every other case — no behavior change when per-direction ratios are absent, and no breakdown query is issued then.

## 4. Invoice — Differentiated description

- [x] 4.1 Inject `IOptions<PlatformLlmOptions>` into `DefaultInvoiceGenerationService` (mirror the quota service ctor); cache a `bool _perDirectionPricing`.
- [x] 4.2 In the line-item build, when `rate.UsageType == UsageType.AiAnalysis && _perDirectionPricing`, set `Description = "AiAnalysis (input/output pricing)"`; else keep `rate.UsageType.ToString()`. Make the `Calculate*LineItem` helpers instance methods (or thread the description in) since they are currently `static`. Amount/quantity unchanged.

## 5. Tests

- [x] 5.1 `GetAiTokenBreakdownAsync_ShouldSumSplitAndUnsplitBuckets_WhenMixedMetadata` (InMemory store; + a Postgres Testcontainers test mirroring it, incl. a NULL-metadata row counted as unsplit).
- [x] 5.2 `CheckQuotaAsync_ShouldUsePerDirectionCredits_WhenRatiosActive` — stub breakdown 300/100/0, ratios 2000/500 → 0.35 credits; assert against `AiCreditsMonthly`.
- [x] 5.3 `CheckQuotaAsync_ShouldFallBackToFlatRatio_WhenUnsplitTokens` — breakdown 0/0/500, flat 1000 → 0.5 credits.
- [x] 5.4 `CheckQuotaAsync_ShouldSumMixedBuckets_WithoutCrossContamination` — 300/100/500 → 0.85 credits.
- [x] 5.5 `CheckQuotaAsync_ShouldUseFlatTokenPath_WhenPerDirectionRatiosAbsent` — assert `GetAiTokenBreakdownAsync` is NOT called (`DidNotReceive`) and the result matches today's flat behavior.
- [x] 5.6 `CheckQuotaAsync_ShouldExhaust_WhenDifferentiatedCreditsReachLimit` — differentiated sum == `AiCreditsMonthly` → denied with configured `QuotaAction`.
- [x] 5.7 `GenerateAsync_ShouldDescribePerDirectionPricing_WhenRatiosConfigured` — `Description` contains the differentiated indicator.
- [x] 5.8 `GenerateAsync_ShouldUseStandardDescription_WhenFlatRatioOnly` — `Description == "AiAnalysis"`, amount unchanged.

## 6. Verification

- [ ] 6.1 `dotnet build Verbara.Platform.slnx` — zero warnings (TreatWarningsAsErrors, WarningLevel 9999).
- [ ] 6.2 `dotnet test Verbara.Platform.slnx` — all green incl. new tests (Billing.Tests, Api.Tests, Storage.InMemory.Tests, Storage.Postgres.Tests).
- [ ] 6.3 AOT gate: `dotnet publish src/Verbara.Platform.Api -r linux-x64 -c Release -p:PublishAot=true` — no IL2026/IL3050/IL207x; native ELF.
- [ ] 6.4 CI green on the feature branch.
