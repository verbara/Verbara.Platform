# ai-credit-billing — Delta

## MODIFIED Requirements

### Requirement: Allowance-based AI-credit overage line item
For the `AiAnalysis` rate, `DefaultInvoiceGenerationService` SHALL compute the overage against the
per-tenant `TenantQuota.AiCreditsMonthly` allowance expressed **in credits**, NOT against the rate-card
`RateEntry.IncludedQuantity` (which is token-denominated for other usage types). Consumed credits SHALL be
derived on the same basis as `DefaultQuotaEnforcementService`: per-direction credits
(`InputTokens/InputCreditTokenRatio + OutputTokens/OutputCreditTokenRatio + UnsplitTokens/CreditTokenRatio`)
when both `PlatformLlmOptions` direction ratios are set and `> 0`, otherwise flat
(`tokens / CreditTokenRatio`). The emitted `AiAnalysis` `InvoiceLineItem` SHALL set
`IncludedQuantity = AiCreditsMonthly`, `OverageQuantity = max(0, consumedCredits − AiCreditsMonthly)`,
`Quantity = consumedCredits`, `UnitPrice` from the rate-card `AiAnalysis` `RateEntry`, and
`Amount = OverageQuantity × UnitPrice`. For `AiAnalysis` this allowance-based computation REPLACES the
generic rate-card-`IncludedQuantity` overage so the two never double-count. Computation SHALL use only
usage records whose timestamps fall within `[periodStart, periodEnd)`.

This usage-record account applies while the credit-ledger **invoice-read flag is OFF** (the
shadow-reconciliation window). When the invoice-read flag is **ON**, `BuildAiCreditLineItemAsync`
SHALL derive `OverageQuantity` as `Σ |PostPaid debits|` per the `ai-credit-ledger` capability and
SHALL stop reading `usage_records` for that amount; at flip time the two computations are equal per
the ledger's shadow-gate requirement, so the line-item shape and amount are unchanged.

#### Scenario: Tenant within allowance produces no overage amount
- **GIVEN** a tenant with `AiCreditsMonthly = 1000` and 800 credits consumed in the period
- **WHEN** invoice generation runs for that period and the rate card has an `AiAnalysis` `RateEntry`
- **THEN** the `AiAnalysis` line item SHALL have `IncludedQuantity = 1000`, `OverageQuantity = 0`, and `Amount = 0`

#### Scenario: Tenant exceeds allowance produces positive overage line item
- **GIVEN** a tenant with `AiCreditsMonthly = 1000`, 1350 credits consumed, and an `AiAnalysis` rate `UnitPrice = 0.02`
- **WHEN** invoice generation runs for that period
- **THEN** the `AiAnalysis` line item SHALL have `IncludedQuantity = 1000`, `OverageQuantity = 350`, and `Amount = 350 × 0.02`

#### Scenario: Tenant with null allowance is billed pay-as-you-go
- **GIVEN** a tenant with `AiCreditsMonthly = null`
- **WHEN** invoice generation runs for that period
- **THEN** the full consumed-credit quantity SHALL be billed at the rate-card `AiAnalysis` `UnitPrice` with `IncludedQuantity = 0`

#### Scenario: No AiAnalysis rate entry produces no overage line item
- **GIVEN** a rate card with no `RateEntry` whose `UsageType = AiAnalysis`
- **WHEN** invoice generation runs
- **THEN** the invoice SHALL contain no `AiAnalysis` line item, regardless of consumption

#### Scenario: Invoice-read flag ON derives overage from PostPaid debits
- **GIVEN** the credit-ledger invoice-read flag is ON for a tenant seeded into the ledger with `AiCreditsMonthly = 1000` and 1350 credits consumed
- **WHEN** invoice generation runs for the period
- **THEN** the `AiAnalysis` line item's `OverageQuantity = Σ |PostPaid debits| = 350`, equal to this requirement's usage-record computation for the same inputs
