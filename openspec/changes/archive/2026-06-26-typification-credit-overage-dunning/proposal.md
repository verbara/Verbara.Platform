---
tier: MEDIANO
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

P2c.2 introduced metered AI-credit recording (`UsageType.AiAnalysis`), so consumption beyond
`TenantQuota.AiCreditsMonthly` is faithfully tracked in `UsageRecord` — but it carries **no financial
consequence**: nothing converts the overage into an invoiced line item, nothing notifies the operator
or tenant when the monthly allowance is being burned through, and — as grounding against the real code
revealed — **the dunning pipeline does not actually fire in production at all.**

`PostgresInvoiceStore` persists neither `due_date` nor `payment_status` (INSERT/SELECT cover only
`invoice_id … paid_at`), `ManagementBillingEndpoints.IssueInvoice` calls `UpdateStatusAsync` and **never
sets `DueDate`**, and `DunningService` Phase 1 skips every invoice whose `DueDate is null`
(`DunningService.cs:97`). Net effect under Postgres: no invoice ever has a `DueDate`, dunning never
triggers, and any `PaymentStatus` mutation a dunning cycle makes is lost on the next read. (The in-memory
store round-trips the whole object, which is why existing tests are green.) Overage is therefore passive
**and** the escalation machinery is inert. This change makes the full path — overage detection →
invoice line item → grace-gated issuance → dunning escalation → threshold notification — work
end-to-end in production.

## What Changes

- **AI-credit overage line item (allowance-based):** `DefaultInvoiceGenerationService` SHALL emit, for the
  `AiAnalysis` rate, an overage line item computed against the per-tenant **`TenantQuota.AiCreditsMonthly`
  allowance in credits** (not the rate-card `IncludedQuantity` in tokens). `IncludedQuantity =
  AiCreditsMonthly`, `OverageQuantity = max(0, consumedCredits − AiCreditsMonthly)`, `UnitPrice` from the
  rate-card `AiAnalysis` `RateEntry`, `Amount = OverageQuantity × UnitPrice`. Consumed credits are derived
  on the **same basis as `DefaultQuotaEnforcementService`** (per-direction credits when both
  `PlatformLlmOptions` ratios are set, else flat `CreditTokenRatio`). For `AiAnalysis` this **replaces**
  the generic rate-card-`IncludedQuantity` overage so the two definitions never double-count.
  `AiCreditsMonthly = null` → pay-as-you-go (`IncludedQuantity = 0`, full consumption billed). This adds an
  `ITenantQuotaStore` dependency to `DefaultInvoiceGenerationService`.
- **Invoice due-date & payment-status persistence (the load-bearing fix):** migration adds `due_date` and
  `payment_status` columns to the `invoices` table; `PostgresInvoiceStore` persists and rehydrates both;
  `Invoice.DueDate` is set at issue time. Without this the dunning half of the change is a no-op in prod.
- **Grace-gated overage invoice issuance:** a new `OverageInvoiceIssuanceWorker : BackgroundService`
  (mirroring `DunningService`'s shape — keyed `ResiliencePolicy`, scope-per-cycle, internal per-cycle hook
  for tests) issues `Draft` invoices that carry an `AiAnalysis` overage line item once
  `PeriodEnd + DunningConfig.OverageGraceDays ≤ clock.UtcNow`: `Status = Issued`, `IssuedAt = now`,
  `DueDate = now + DunningConfig.PaymentTermDays`. (Invoice **generation** remains driven by the existing
  manual/external path — `ManagementBillingEndpoints.GenerateInvoice` → `GenerateAsync`, now overage-aware;
  tenant-wide auto-generation is explicitly out of scope.)
- **Dunning processes overage invoices:** the existing `DunningService` picks up issued overage invoices
  identically to subscription invoices, escalating **`PaymentStatus`** (`Current → Overdue → Delinquent →
  WrittenOff`) and tenant `TenantStatus` (`Warning → Degraded → Suspended → PendingDeletion`) while
  `InvoiceStatus` stays `Issued`. No new dunning logic — only the persistence fix makes it fire.
- **Overage threshold notifications:** a hook in **`BillingTypificationCreditMeter.RecordAsync`** (the only
  `AiAnalysis` recording funnel, `Api/Services`, singleton) dispatches a notification when cumulative
  period consumption **first crosses** 80 % (warning) and 100 % (critical) of `AiCreditsMonthly`.
  Idempotency is **stateless straddle detection** — fire only when `previousCredits < thresholdCredits ≤
  currentCredits` for the record that pushes over — so **no new state table/migration is required**
  (`NotificationService`'s existing 5-minute `tenantId:type` dedup window is a backstop). Notifications
  reuse the already-registered `billing.quota_warning` (Warning) and `billing.quota_exceeded` (Critical)
  types, dispatched via `INotificationService.CreateAsync`; recipients are resolved by the registry's
  `TargetRoles` (there is no recipient parameter / "billing contact" concept). `AiCreditsMonthly = null`
  → no thresholds.
- **`DunningConfig` extension:** add `OverageGraceDays` (default 3) and `PaymentTermDays` (default 14),
  bound via the existing **manual `int.TryParse`** idiom in `Program.cs` (the repo is Native AOT — no
  reflection `.Bind()`; there is no `Dunning` section in appsettings, values come from C# defaults).

## Capabilities

### New Capabilities

- `ai-credit-billing`: allowance-based AI-credit overage line items, invoice due-date/payment-status
  persistence, grace-gated overage invoice issuance, dunning over overage invoices, and stateless
  threshold notifications at 80 %/100 % of the monthly credit allowance.

### Modified Capabilities

- `ai-credit-billing`: delta spec at `specs/ai-credit-billing/spec.md` (new capability — the file is
  entirely additive).

## Impact

- **`Verbara.Platform.Billing`:** `DefaultInvoiceGenerationService` (+`ITenantQuotaStore` ctor dep,
  allowance-based `AiAnalysis` overage); `DunningConfig` (+`OverageGraceDays`, +`PaymentTermDays`); new
  `OverageInvoiceIssuanceWorker : BackgroundService`. No change to `DunningService`'s escalation logic.
- **`Verbara.Platform.Api`:** `BillingTypificationCreditMeter` (+threshold-notification hook,
  +`INotificationService`/credit-usage deps); `Program.cs` (manual-bind the two new `DunningConfig` fields,
  `AddHostedService<OverageInvoiceIssuanceWorker>` + its keyed `ResiliencePolicy`, set `DueDate` at issue
  in `ManagementBillingEndpoints.IssueInvoice`).
- **`Verbara.Platform.Core/Notifications`:** verify/keep `billing.quota_warning` + `billing.quota_exceeded`
  registry entries (add new `billing.credit_*` types only if those clash with an existing producer).
- **`Verbara.Platform.Storage.Postgres`:** new migration (`due_date` + `payment_status` columns on
  `invoices`); `PostgresInvoiceStore` INSERT/SELECT/Map + a payment-status/due-date round-trip so
  `DunningService` mutations persist.
- **`Verbara.Platform.Storage.InMemory`:** `InMemoryInvoiceStore` already round-trips the whole object — no
  change expected (verify `ListByStatusAsync`/`DueDate` behaviour).
- **No Sdk / Sdk.Pro changes required.** `TenantStatus` is consumed from the existing
  `Verbara.Sdk.Pro.MultiTenant` package (not extended). All work is confined to `Verbara.Platform`.
- **`Verbara.Platform.Web`:** out of scope (a future change may surface overage alerts in the admin portal).
