## ADDED Requirements

### Requirement: Per-direction credit ratio configuration
`PlatformLlmOptions` SHALL expose two optional per-direction ratio properties — `InputCreditTokenRatio` and `OutputCreditTokenRatio` — alongside the existing `CreditTokenRatio`. When both per-direction ratios are set (non-null, greater than zero), the credit aggregation path MUST use them in place of the flat ratio. When either per-direction ratio is absent or zero, the system SHALL fall back to `CreditTokenRatio` for that computation.

#### Scenario: Both per-direction ratios configured
- **GIVEN** `PlatformLlmOptions` has `InputCreditTokenRatio = 2000` and `OutputCreditTokenRatio = 500`
- **WHEN** the credit aggregation path resolves the effective ratio
- **THEN** input tokens are divided by 2000 and output tokens are divided by 500 to yield the credit totals

#### Scenario: Only flat ratio configured (legacy default)
- **GIVEN** `PlatformLlmOptions` has `CreditTokenRatio = 1000` and `InputCreditTokenRatio` and `OutputCreditTokenRatio` are null
- **WHEN** the credit aggregation path resolves the effective ratio
- **THEN** total tokens are divided by 1000 using the flat ratio, preserving backward-compatible behavior

### Requirement: Input/output-differentiated credit aggregation
The quota enforcement service SHALL compute the AI Credit equivalent for `UsageType.AiAnalysis` records using the per-direction token counts stored in `UsageRecord.Metadata` (`inputTokens`, `outputTokens`) when per-direction ratios are configured. For each record the system MUST: (a) parse `inputTokens` and `outputTokens` from metadata; (b) apply `InputCreditTokenRatio` and `OutputCreditTokenRatio` respectively; (c) sum the two partial credit values as the record's credit contribution. Records whose metadata does not contain both keys MUST fall back to dividing the record's `Quantity` (total tokens) by the flat `CreditTokenRatio`.

#### Scenario: Record with full metadata and per-direction ratios active
- **GIVEN** a `UsageRecord` with `Quantity = 400`, `Metadata["inputTokens"] = "300"`, `Metadata["outputTokens"] = "100"`, and `InputCreditTokenRatio = 2000`, `OutputCreditTokenRatio = 500`
- **WHEN** the aggregation path computes this record's credit contribution
- **THEN** the contribution is `(300 / 2000) + (100 / 500) = 0.15 + 0.20 = 0.35` credits

#### Scenario: Record without metadata falls back to flat ratio
- **GIVEN** a `UsageRecord` with `Quantity = 500` and no `Metadata` (or metadata without `inputTokens`/`outputTokens` keys), and `CreditTokenRatio = 1000`
- **WHEN** the aggregation path computes this record's credit contribution
- **THEN** the contribution is `500 / 1000 = 0.5` credits, using the flat fallback

#### Scenario: Mixed-record aggregation
- **GIVEN** a period containing two `UsageRecord` rows — one with metadata and per-direction ratios active, one without metadata — with a flat `CreditTokenRatio = 1000`, `InputCreditTokenRatio = 2000`, `OutputCreditTokenRatio = 500`
- **WHEN** the aggregation path sums credits for the period
- **THEN** each record is evaluated by its own applicable path (per-direction or flat) and the results are summed without cross-contamination

#### Scenario: Quota enforcement uses differentiated credit total
- **GIVEN** a tenant with `AiCreditsMonthly = 10`, per-direction ratios configured, and consumed records whose differentiated-credit sum equals 10 credits
- **WHEN** `CheckQuotaAsync` is called for `UsageType.AiAnalysis`
- **THEN** the quota is reported as exhausted and the configured `QuotaAction` is applied

### Requirement: Invoice line-item reflects differentiated pricing
When per-direction ratios are configured, the `AiAnalysis` invoice line-item MUST reflect that differentiated pricing was applied. The line-item description SHALL include an indication (e.g., `"AI Analysis (input/output pricing)"`) when split ratios are active, and the standard description (`"AiAnalysis"`) when only the flat ratio is in use.

#### Scenario: Invoice generated with per-direction ratios
- **GIVEN** per-direction ratios `InputCreditTokenRatio` and `OutputCreditTokenRatio` are both configured
- **WHEN** `DefaultInvoiceGenerationService.GenerateAsync` produces the `AiAnalysis` line-item
- **THEN** the `InvoiceLineItem.Description` reflects that input/output differentiated pricing was applied

#### Scenario: Invoice generated with flat ratio (no change to existing behavior)
- **GIVEN** only `CreditTokenRatio` is set and per-direction ratios are absent
- **WHEN** `DefaultInvoiceGenerationService.GenerateAsync` produces the `AiAnalysis` line-item
- **THEN** the `InvoiceLineItem.Description` is unchanged from the current behavior (no differentiated-pricing indicator)

## Architectural Risk

**Level:** LOW

**Affected:** `Verbara.Platform.Billing` (aggregation + invoicing), `Verbara.Platform.Llm` (options class). No cross-repo impact — `PlatformLlmOptions` is host-internal and not part of the Sdk/Sdk.Pro public surface; `UsageRecord` and `UsageRecordStore` are unchanged.

**Mitigation:** The fallback path (flat `CreditTokenRatio` when metadata is absent) is exercised by all existing records, so the feature is entirely opt-in via configuration. No DB migration is required. The `BanDapperPackageReferences` guard and AOT constraints are unaffected — all new logic is reflection-free arithmetic on parsed strings, with no new serialized DTOs.
