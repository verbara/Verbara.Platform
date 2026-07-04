# ai-credit-metering Specification

## Purpose
The pricing basis that turns platform-managed LLM token usage into AI credits: optional
input/output-differentiated credit ratios with a flat fallback, the aggregated per-direction token
breakdown query, and the invoice line-item pricing marker. Feeds the legacy quota path
(`typification-platform-llm`) and the ledger meter (`ai-credit-ledger`) on the same
`PerDirectionActive` basis; owns how usage is priced, never the enforcement outcome.
## Requirements
### Requirement: Per-direction credit ratio configuration
`PlatformLlmOptions` SHALL expose two optional per-direction ratio properties — `InputCreditTokenRatio` and `OutputCreditTokenRatio` (both `long?`) — alongside the existing `CreditTokenRatio` (`long`, default 1000). Per-direction pricing is ACTIVE only when BOTH per-direction ratios are non-null and greater than zero; otherwise the system falls back to the flat `CreditTokenRatio`. Both keys are read from the `Llm:Platform` config section in `Program.cs` via the existing AOT-safe per-key `long.TryParse` pattern (no `IConfiguration.Bind`).

#### Scenario: Both per-direction ratios configured
- **GIVEN** `PlatformLlmOptions` has `InputCreditTokenRatio = 2000` and `OutputCreditTokenRatio = 500`
- **WHEN** the credit aggregation path resolves the effective pricing
- **THEN** input tokens are divided by 2000 and output tokens by 500 to yield credit totals

#### Scenario: Only flat ratio configured (legacy default)
- **GIVEN** `PlatformLlmOptions` has `CreditTokenRatio = 1000` and `InputCreditTokenRatio`/`OutputCreditTokenRatio` null (or zero)
- **WHEN** the credit aggregation path resolves the effective pricing
- **THEN** the existing flat token-vs-token quota path is used unchanged, preserving backward-compatible behavior

### Requirement: Aggregated per-direction token breakdown query
`IUsageRecordStore` SHALL expose a method that returns, for a tenant + `UsageType` + period, the aggregate token sums needed for differentiated credit pricing — without enumerating individual records on the caller side. The method returns three decimals: the sum of `inputTokens` and the sum of `outputTokens` over records whose `Metadata` contains BOTH keys, and the sum of `Quantity` over records lacking the split (NULL metadata or missing either key — the flat-fallback bucket). This decomposition is mathematically equivalent to per-record evaluation (`Σ(input/inRatio + output/outRatio)` over split records `+ Σ(quantity/flatRatio)` over unsplit records) while remaining a single aggregation on the database hot path.

#### Scenario: Postgres aggregation sums split and unsplit buckets
- **GIVEN** AiAnalysis usage records in a period, some with `Metadata["inputTokens"]`/`["outputTokens"]` and some with NULL/absent metadata
- **WHEN** the breakdown is queried for that tenant + `UsageType.AiAnalysis` + period
- **THEN** it returns `InputTokens` = Σ of split records' `inputTokens`, `OutputTokens` = Σ of split records' `outputTokens`, and `UnsplitTokens` = Σ `Quantity` of records lacking the split — NULL-metadata records counting toward `UnsplitTokens`, never silently dropped
- **THEN** the InMemory store implementation returns identical results for the same data

### Requirement: Input/output-differentiated credit aggregation in quota enforcement
When per-direction ratios are active, the quota enforcement service SHALL compute the AI-Credit equivalent for `UsageType.AiAnalysis` in CREDITS (not tokens): current usage credits = `InputTokens/InputCreditTokenRatio + OutputTokens/OutputCreditTokenRatio + UnsplitTokens/CreditTokenRatio` from the breakdown query, compared against the limit `TenantQuota.AiCreditsMonthly` (in credits, NOT multiplied by a ratio). The `additionalQuantity` parameter (nominal tokens) is converted to credits via the flat `CreditTokenRatio` for the projection. When per-direction ratios are NOT active, the existing flat path (`limit = AiCreditsMonthly × CreditTokenRatio` tokens, compared to summed `TotalQuantity` tokens) is used UNCHANGED.

#### Scenario: Split record contributes per-direction credits
- **GIVEN** a breakdown of `InputTokens = 300`, `OutputTokens = 100`, `UnsplitTokens = 0`, with `InputCreditTokenRatio = 2000`, `OutputCreditTokenRatio = 500`
- **WHEN** the quota service computes current usage credits
- **THEN** the total is `(300 / 2000) + (100 / 500) = 0.15 + 0.20 = 0.35` credits

#### Scenario: Unsplit tokens fall back to the flat ratio
- **GIVEN** a breakdown of `InputTokens = 0`, `OutputTokens = 0`, `UnsplitTokens = 500`, with `CreditTokenRatio = 1000` and per-direction ratios configured
- **WHEN** the quota service computes current usage credits
- **THEN** the unsplit contribution is `500 / 1000 = 0.5` credits, using the flat fallback

#### Scenario: Mixed buckets summed without cross-contamination
- **GIVEN** a breakdown of `InputTokens = 300`, `OutputTokens = 100`, `UnsplitTokens = 500`, with `CreditTokenRatio = 1000`, `InputCreditTokenRatio = 2000`, `OutputCreditTokenRatio = 500`
- **WHEN** the quota service computes current usage credits
- **THEN** the total is `0.15 + 0.20 + 0.50 = 0.85` credits — split and unsplit buckets each use their own ratio

#### Scenario: Quota enforcement uses differentiated credit total
- **GIVEN** a tenant with `AiCreditsMonthly = 10`, per-direction ratios configured, and a breakdown whose differentiated-credit sum equals 10 credits
- **WHEN** `CheckQuotaAsync` is called for `UsageType.AiAnalysis` with a nominal `additionalQuantity`
- **THEN** the projected credit usage exceeds the 10-credit limit and the configured `QuotaAction` (SoftBlock/HardBlock) is applied

#### Scenario: Flat path unchanged when per-direction ratios absent
- **GIVEN** only `CreditTokenRatio = 1000` is set (per-direction ratios null)
- **WHEN** `CheckQuotaAsync` is called for `UsageType.AiAnalysis`
- **THEN** the service uses the existing summary-based token comparison (`AiCreditsMonthly × CreditTokenRatio` vs summed `TotalQuantity`), byte-identical to current behavior — no breakdown query is issued

### Requirement: Invoice line-item reflects differentiated pricing
When per-direction ratios are configured, the `AiAnalysis` invoice line-item description MUST indicate that input/output differentiated pricing applies (e.g. `"AiAnalysis (input/output pricing)"`); otherwise the description is the unchanged enum name (`"AiAnalysis"`). The line-item AMOUNT is unchanged — it remains rate-card-driven (`overage × UnitPrice` on metered `TotalQuantity`), because the rate card prices tokens; differentiated credit pricing governs the AI-Credit allowance (quota), not the token rate card. `DefaultInvoiceGenerationService` gains `IOptions<PlatformLlmOptions>` to detect active per-direction ratios.

#### Scenario: Invoice generated with per-direction ratios
- **GIVEN** both `InputCreditTokenRatio` and `OutputCreditTokenRatio` are configured and the rate card has an `AiAnalysis` rate
- **WHEN** `DefaultInvoiceGenerationService.GenerateAsync` produces the `AiAnalysis` line-item
- **THEN** the `InvoiceLineItem.Description` indicates input/output differentiated pricing was applied

#### Scenario: Invoice generated with flat ratio (no change to existing behavior)
- **GIVEN** only `CreditTokenRatio` is set (per-direction ratios absent)
- **WHEN** `DefaultInvoiceGenerationService.GenerateAsync` produces the `AiAnalysis` line-item
- **THEN** the `InvoiceLineItem.Description` is `"AiAnalysis"` and the amount is unchanged from current behavior

