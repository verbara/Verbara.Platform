---
tier: MEDIANO
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

P2c.2 converts AI Credits from total token counts at a single flat ratio (`CreditTokenRatio`), but LLM providers charge input and output tokens at materially different rates — output tokens typically cost 3–5x more than input tokens. Because `BillingTypificationCreditMeter` already writes `inputTokens` and `outputTokens` as forward-compat keys into `UsageRecord.Metadata`, differentiated pricing can be activated with no DB migration and no backfill: the data is already present for every record produced since P2c.2 shipped.

## What Changes

- Add `InputCreditTokenRatio` and `OutputCreditTokenRatio` properties to `PlatformLlmOptions`; keep `CreditTokenRatio` as the fallback for records whose metadata lacks split counts.
- In the credit aggregation / quota-enforcement path (`DefaultQuotaEnforcementService`), compute AI Credits using per-direction ratios when `inputTokens`/`outputTokens` metadata is present on each `UsageRecord`, falling back to the flat `CreditTokenRatio` on the aggregate `TotalQuantity` when metadata is absent (older records).
- In `DefaultInvoiceGenerationService`, reflect differentiated pricing in the `AiAnalysis` line-item description when split ratios are configured.
- No DB schema change required — `UsageRecord.Metadata` is already a `Dictionary<string, string>` (jsonb column) storing the per-direction counts.

## Capabilities

### New Capabilities

_(none — this change activates a pricing dimension that is already latent; no wholly new capability boundary is introduced)_

### Modified Capabilities

- `ai-credit-metering`: Credit aggregation and invoice line-item generation SHALL support per-direction (input/output) token ratios in addition to the existing flat total-token ratio, with automatic fallback for legacy records.

## Impact

- **Verbara.Platform.Billing** — `DefaultQuotaEnforcementService` (credit-to-token limit conversion), `DefaultInvoiceGenerationService` (line-item amount + description for `AiAnalysis`).
- **Verbara.Platform.Llm** — `PlatformLlmOptions` gains two new optional ratio properties.
- **No DB migration** — `UsageRecord.Metadata` already stores `inputTokens`/`outputTokens` as string pairs in jsonb.
- **No Platform.Web change** — credit balance and quota responses are numeric; the UI component reads them unchanged.
- **No SDK / Pro change** — `PlatformLlmOptions` is a Platform-internal options class, not part of the public SDK surface.
