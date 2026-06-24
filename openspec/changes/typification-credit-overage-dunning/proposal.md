---
tier: MEDIANO
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

P2c.2 introduced metered AI-credit recording (`AiAnalysis` usage type), so overage beyond `AiCreditsMonthly` is faithfully tracked in `UsageRecord` — but no downstream pipeline converts that overage into an invoiced line item, triggers dunning when the resulting invoice goes unpaid, or notifies the operator/tenant when the quota threshold is crossed. Overage is therefore passive: it accumulates silently and carries no financial consequence until a human manually generates an invoice.

## What Changes

- **Overage computation:** `DefaultInvoiceGenerationService` SHALL detect `AiAnalysis` credits consumed beyond `TenantQuota.AiCreditsMonthly` for a billing period and emit a dedicated overage `InvoiceLineItem` (`OverageQuantity > 0`).
- **Invoice → dunning wiring:** The existing `DunningService` already monitors `InvoiceStatus.Issued` invoices past their `DueDate`; overage invoices issued for AI credits SHALL flow through this pipeline automatically without new dunning logic.
- **Overage threshold notification:** A new `IOverageNotificationService` SHALL emit an operator and tenant notification when cumulative `AiAnalysis` usage in the current period first crosses a configurable percentage of `AiCreditsMonthly` (default 80 %) and again at 100 %.
- **`DunningConfig` extension:** Add `OverageGraceDays` (days after period end before the overage invoice is issued, default 3) to allow disputed usage to be resolved before dunning starts.

## Capabilities

### New Capabilities

- `ai-credit-billing`: Automatic overage computation, overage invoice generation, and threshold notifications for AI credits consumed beyond the monthly allowance; dunning via the existing `DunningService` pipeline.

### Modified Capabilities

- `ai-credit-billing`: Delta spec at `specs/ai-credit-billing/spec.md` (this capability is new in this change, so the file is entirely additive).

## Impact

- **`Verbara.Platform.Billing`:** `DefaultInvoiceGenerationService` (overage line item logic), `DunningConfig` (new `OverageGraceDays` field), new `IOverageNotificationService` + default implementation.
- **`Verbara.Platform.Billing`:** `ServiceCollectionExtensions` to register `IOverageNotificationService`.
- **`Verbara.Platform.Notifications`** (consumed in-process): used by `IOverageNotificationService` to dispatch operator and tenant notifications.
- **No Sdk / Sdk.Pro changes required.** All work is confined to `Verbara.Platform`.
- **`Verbara.Platform.Web`:** Optional future work — surface overage alerts in the billing section of the admin portal (out of scope for this change).
