# ai-credit-billing Specification

## Purpose
TBD - created by archiving change typification-credit-overage-dunning. Update Purpose after archive.
## Requirements
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

### Requirement: Invoice due-date and payment-status persistence
The `invoices` storage schema SHALL persist `due_date` (nullable timestamptz) and `payment_status`
(non-null smallint, default `0` = `Current`). `PostgresInvoiceStore` SHALL write both on save and rehydrate
both on read, and a `PaymentStatus` mutation performed by a dunning cycle SHALL survive a subsequent reload.
This requirement is the prerequisite that makes dunning operate under Postgres at all.

#### Scenario: DueDate and PaymentStatus round-trip through Postgres
- **GIVEN** an invoice saved with `DueDate = T` and `PaymentStatus = Overdue`
- **WHEN** it is re-read via `GetByIdAsync` / `ListByStatusAsync`
- **THEN** the rehydrated invoice SHALL have `DueDate = T` and `PaymentStatus = Overdue`

#### Scenario: Legacy rows without the columns default safely
- **GIVEN** an invoice row written before the migration (no `payment_status` value)
- **WHEN** the migration runs and the row is read
- **THEN** `PaymentStatus` SHALL be `Current` and `DueDate` SHALL be `null` (dunning skips it, as today)

### Requirement: Grace-gated overage invoice issuance
A new `OverageInvoiceIssuanceWorker : BackgroundService` SHALL, on the `DunningConfig.CheckIntervalHours`
cadence, transition `Draft` invoices that carry an `AiAnalysis` overage line item
(`OverageQuantity > 0`) to `Issued` once `PeriodEnd + DunningConfig.OverageGraceDays ≤ clock.UtcNow`,
setting `IssuedAt = clock.UtcNow` and `DueDate = clock.UtcNow + DunningConfig.PaymentTermDays`. It SHALL NOT
issue drafts whose grace period has not yet elapsed, and SHALL NOT issue drafts lacking an `AiAnalysis`
overage line item. The worker SHALL mirror `DunningService`'s structure (registered via
`AddHostedService`, its own keyed `ResiliencePolicy`, a scope per cycle, and an `internal` per-cycle method
so tests drive it deterministically without the timer loop).

#### Scenario: Grace period not yet elapsed — invoice stays Draft
- **GIVEN** a Draft overage invoice with `PeriodEnd = T` and `OverageGraceDays = 3`
- **WHEN** the worker runs at `T + 2 days`
- **THEN** the invoice SHALL remain `InvoiceStatus.Draft` with `DueDate = null`

#### Scenario: Grace period elapsed — invoice is issued with a due date
- **GIVEN** a Draft overage invoice with `PeriodEnd = T`, `OverageGraceDays = 3`, `PaymentTermDays = 14`
- **WHEN** the worker runs at `T + 4 days`
- **THEN** the invoice SHALL become `InvoiceStatus.Issued` with `IssuedAt = now` and `DueDate = now + 14 days`

#### Scenario: Non-overage draft is left untouched
- **GIVEN** a Draft invoice with no `AiAnalysis` overage line item, grace elapsed
- **WHEN** the worker runs
- **THEN** the invoice SHALL remain `Draft` (the worker only issues overage drafts)

### Requirement: Dunning escalation over overage invoices
The existing `DunningService` SHALL process issued overage invoices identically to subscription invoices:
detecting `DueDate < now`, creating a `DunningRecord`, setting `Invoice.PaymentStatus = Overdue`, and
escalating tenant `TenantStatus` (`Warning → Degraded → Suspended → PendingDeletion`) per `DunningConfig`
thresholds, while `InvoiceStatus` remains `Issued`. No modification to `DunningService` escalation logic is
required — only the persistence fix makes the cycle observable under Postgres.

#### Scenario: Unpaid issued overage invoice enters dunning
- **GIVEN** an `Issued` overage invoice whose `DueDate` has passed and no existing `DunningRecord`
- **WHEN** `DunningService.ProcessDunningCycleAsync` runs
- **THEN** a `DunningRecord` SHALL be created with `CurrentStage = TenantStatus.Warning`
- **AND** the invoice `PaymentStatus` SHALL be set to `PaymentStatus.Overdue` and persisted
- **AND** the tenant status SHALL be set to `TenantStatus.Warning`

#### Scenario: Paid overage invoice does not enter dunning
- **GIVEN** an overage invoice with `InvoiceStatus.Paid`
- **WHEN** `DunningService.ProcessDunningCycleAsync` runs
- **THEN** no `DunningRecord` SHALL be created for that invoice (it is not in the `Issued` set)

### Requirement: Overage threshold notifications
`BillingTypificationCreditMeter.RecordAsync` SHALL, after recording each `AiAnalysis` usage record for a
tenant whose `AiCreditsMonthly` is non-null, dispatch a notification when cumulative current-period credit
consumption **first crosses** a warning threshold (80 % of `AiCreditsMonthly`) and a critical threshold
(100 %). Idempotency SHALL be achieved by **stateless straddle detection**: a threshold notification fires
only when `previousCredits < thresholdCredits ≤ currentCredits`, where `currentCredits` is the post-record
period total and `previousCredits = currentCredits − thisRecordCredits`; no per-tenant/period/threshold
state store is introduced. The warning threshold SHALL dispatch the registered `billing.quota_warning`
type and the critical threshold the `billing.quota_exceeded` type via `INotificationService.CreateAsync`
(recipients resolved by the registry `TargetRoles`; there is no recipient parameter). A tenant with
`AiCreditsMonthly = null` SHALL receive no threshold notifications.

#### Scenario: Usage crosses the warning threshold for the first time
- **GIVEN** a tenant with `AiCreditsMonthly = 1000` and 799 credits already consumed this period
- **WHEN** a new `AiAnalysis` record brings the period total to 800 credits
- **THEN** `INotificationService.CreateAsync` SHALL be invoked once with type `billing.quota_warning`

#### Scenario: Same threshold not re-notified within the same period
- **GIVEN** the 80 % crossing already fired for the current period
- **WHEN** a further `AiAnalysis` record raises the total from 820 to 860 credits (still < 100 %)
- **THEN** no additional `billing.quota_warning` notification SHALL be dispatched (the straddle condition is false)

#### Scenario: Usage crosses 100 % triggers the critical notification
- **GIVEN** a tenant with `AiCreditsMonthly = 1000` and 990 credits already consumed
- **WHEN** a new `AiAnalysis` record brings the total to 1010 credits
- **THEN** `INotificationService.CreateAsync` SHALL be invoked once with type `billing.quota_exceeded`

#### Scenario: Tenant with unlimited allowance receives no threshold notifications
- **GIVEN** a tenant with `AiCreditsMonthly = null`
- **WHEN** any `AiAnalysis` usage is recorded
- **THEN** no threshold notification SHALL be dispatched

### Requirement: DunningConfig OverageGraceDays and PaymentTermDays fields
`DunningConfig` SHALL expose `OverageGraceDays` (`int`, default `3`) and `PaymentTermDays` (`int`, default
`14`). Negative values SHALL be treated as `0`. Both SHALL be bound from the `Dunning` configuration
section using the existing manual `int.TryParse` idiom in `Program.cs` (Native AOT — no reflection
`.Bind()`); when a key is absent the C# default applies.

#### Scenario: Explicit configuration binds
- **GIVEN** `appsettings.json` contains `"Dunning": { "OverageGraceDays": 5, "PaymentTermDays": 30 }`
- **WHEN** the host starts and `DunningConfig` is bound
- **THEN** `OverageGraceDays = 5` and `PaymentTermDays = 30`

#### Scenario: Missing keys use defaults
- **GIVEN** `appsettings.json` specifies neither key
- **WHEN** the host starts and `DunningConfig` is bound
- **THEN** `OverageGraceDays = 3` and `PaymentTermDays = 14`

