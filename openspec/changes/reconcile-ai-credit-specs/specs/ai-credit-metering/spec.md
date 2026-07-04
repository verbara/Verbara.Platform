# ai-credit-metering — Delta

## MODIFIED Requirements

### Requirement: Input/output-differentiated credit aggregation in quota enforcement
When per-direction ratios are active, the quota enforcement service SHALL compute the AI-Credit equivalent for `UsageType.AiAnalysis` in CREDITS (not tokens): current usage credits = `InputTokens/InputCreditTokenRatio + OutputTokens/OutputCreditTokenRatio + UnsplitTokens/CreditTokenRatio` from the breakdown query, compared against the limit `TenantQuota.AiCreditsMonthly` (in credits, NOT multiplied by a ratio). The `additionalQuantity` parameter (nominal tokens) is converted to credits via the flat `CreditTokenRatio` for the projection. When per-direction ratios are NOT active, the existing flat path (`limit = AiCreditsMonthly × CreditTokenRatio` tokens, compared to summed `TotalQuantity` tokens) is used UNCHANGED.

This computation is the **pricing basis** (tokens → credits), not the enforcement outcome owner: it
feeds the legacy quota path in `typification-platform-llm` while the credit-ledger enforcement flag is
OFF, and the same `PerDirectionActive` basis is used by the `ai-credit-ledger` meter and back-fill
(ratio-frozen across the cutover window) when the flag is ON. The enforcement outcome itself is owned
by `ai-credit-ledger` once its flag is ON.

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

#### Scenario: Ledger path prices on the same per-direction basis
- **GIVEN** the credit-ledger enforcement flag is ON and per-direction ratios are active
- **WHEN** the ledger meter converts a classify's tokens to a credit debit
- **THEN** it uses this same differentiated basis (`PerDirectionActive`), so a given usage prices identically on the legacy and ledger paths
