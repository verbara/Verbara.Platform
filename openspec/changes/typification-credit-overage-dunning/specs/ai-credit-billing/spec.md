## ADDED Requirements

### Requirement: Overage credit computation per billing period
The system SHALL compute, for each tenant whose `TenantQuota.AiCreditsMonthly` is non-null, the quantity of `AiAnalysis` usage records that exceed the monthly allowance within a billing period. The overage quantity is defined as `max(0, totalAiAnalysisCredits − AiCreditsMonthly)`. Computation SHALL use only usage records whose timestamps fall within `[periodStart, periodEnd)`.

#### Scenario: Tenant within allowance produces no overage
- **GIVEN** a tenant with `AiCreditsMonthly = 1000` and 800 `AiAnalysis` credits consumed in the period
- **WHEN** invoice generation runs for that period
- **THEN** the invoice SHALL contain no `AiAnalysis` overage line item (or a line item with `OverageQuantity = 0` and `Amount = 0`)

#### Scenario: Tenant exceeds allowance produces positive overage line item
- **GIVEN** a tenant with `AiCreditsMonthly = 1000` and 1350 `AiAnalysis` credits consumed in the period
- **WHEN** invoice generation runs for that period
- **THEN** the invoice SHALL contain an `AiAnalysis` line item where `OverageQuantity = 350`, `Amount = 350 × rateCard.AiAnalysis.UnitPrice`, and `IncludedQuantity = 1000`

#### Scenario: Tenant with null allowance is treated as pay-as-you-go
- **GIVEN** a tenant with `AiCreditsMonthly = null`
- **WHEN** invoice generation runs for that period
- **THEN** the full `AiAnalysis` quantity is billed at the rate-card unit price with `IncludedQuantity = 0`

### Requirement: Overage invoice issuance with grace period
The system SHALL NOT issue an overage invoice immediately at period end. Instead, it SHALL wait `DunningConfig.OverageGraceDays` calendar days after `periodEnd` before setting `InvoiceStatus = Issued` and populating `DueDate`. The default value of `OverageGraceDays` is 3.

#### Scenario: Grace period not yet elapsed — invoice remains Draft
- **GIVEN** a generated overage invoice with `PeriodEnd = T` and `OverageGraceDays = 3`
- **WHEN** the current date is `T + 2 days`
- **THEN** the invoice SHALL remain in `InvoiceStatus.Draft` and SHALL NOT be visible to the dunning pass

#### Scenario: Grace period elapsed — invoice is issued and enters dunning pipeline
- **GIVEN** a generated overage invoice with `PeriodEnd = T` and `OverageGraceDays = 3`
- **WHEN** the current date is `T + 4 days`
- **THEN** the invoice SHALL transition to `InvoiceStatus.Issued` with `IssuedAt = now` and `DueDate = now + rateCard.PaymentTermDays`
- **AND** the existing `DunningService` SHALL pick it up on its next cycle if it becomes overdue

### Requirement: Dunning pipeline processes overage invoices without modification
The existing `DunningService` SHALL process overage invoices (those with an `AiAnalysis` overage line item) identically to subscription invoices: detecting overdue status, creating `DunningRecord` entries, and escalating tenant status through `Warning → Degraded → Suspended → PendingDeletion` according to `DunningConfig` thresholds.

#### Scenario: Unpaid overage invoice triggers Warning stage
- **GIVEN** an issued overage invoice whose `DueDate` has passed
- **WHEN** `DunningService.ProcessDunningCycleAsync` runs
- **THEN** a `DunningRecord` SHALL be created with `CurrentStage = TenantStatus.Warning`
- **AND** the invoice `PaymentStatus` SHALL be updated to `PaymentStatus.Overdue`
- **AND** the tenant status SHALL be set to `TenantStatus.Warning`

#### Scenario: Paid overage invoice does not enter dunning
- **GIVEN** an issued overage invoice marked `InvoiceStatus.Paid`
- **WHEN** `DunningService.ProcessDunningCycleAsync` runs
- **THEN** no `DunningRecord` SHALL be created for that invoice

### Requirement: Overage threshold notifications
The system SHALL emit notifications when cumulative `AiAnalysis` usage within the current billing period first crosses a configurable threshold percentage of `AiCreditsMonthly`. Two thresholds SHALL be supported: a warning threshold (default 80 %) and a critical threshold (default 100 %). Each crossing SHALL generate at most one notification per period per threshold per tenant (idempotent).

#### Scenario: Usage crosses warning threshold for the first time
- **GIVEN** a tenant with `AiCreditsMonthly = 1000` and 799 credits already consumed
- **WHEN** a new `AiAnalysis` usage record is metered that brings the total to 800
- **THEN** `IOverageNotificationService.NotifyThresholdCrossedAsync` SHALL be invoked with `threshold = 80`, `tenantId`, and the current usage total
- **AND** the notification SHALL be dispatched to the operator contact and the tenant billing contact

#### Scenario: Same threshold not re-notified within the same period
- **GIVEN** the 80 % threshold notification was already sent for the current period
- **WHEN** additional `AiAnalysis` usage pushes the total to 850 credits (still below 100 %)
- **THEN** no additional threshold notification SHALL be dispatched for the 80 % threshold

#### Scenario: Usage crosses 100 % threshold triggers critical notification
- **GIVEN** a tenant with `AiCreditsMonthly = 1000` and 999 credits already consumed
- **WHEN** a new `AiAnalysis` usage record brings the total to 1001
- **THEN** `IOverageNotificationService.NotifyThresholdCrossedAsync` SHALL be invoked with `threshold = 100`
- **AND** the notification body SHALL indicate that additional usage will be invoiced as overage

#### Scenario: Tenant with unlimited allowance receives no threshold notifications
- **GIVEN** a tenant with `AiCreditsMonthly = null`
- **WHEN** any `AiAnalysis` usage is metered
- **THEN** no overage threshold notification SHALL be dispatched

### Requirement: DunningConfig OverageGraceDays field
`DunningConfig` SHALL expose an `OverageGraceDays` property of type `int` with a default value of `3`. Values below `0` SHALL be treated as `0` (immediate issuance). This property SHALL be bindable from `appsettings.json` under the `Dunning` configuration section alongside existing fields.

#### Scenario: Configuration with explicit OverageGraceDays
- **GIVEN** `appsettings.json` contains `"Dunning": { "OverageGraceDays": 5 }`
- **WHEN** the host starts and `DunningConfig` is bound
- **THEN** `DunningConfig.OverageGraceDays` SHALL equal `5`

#### Scenario: Missing OverageGraceDays uses default
- **GIVEN** `appsettings.json` does not specify `OverageGraceDays`
- **WHEN** the host starts and `DunningConfig` is bound
- **THEN** `DunningConfig.OverageGraceDays` SHALL equal `3`

## Architectural Risk

**Level:** LOW

**Affected:** `Verbara.Platform.Billing` (invoice generation, dunning config, new notification service); `Verbara.Platform.Api` (DI registration); existing `DunningService` execution path (read-only from its perspective — overage invoices are standard `Invoice` rows).

**Mitigation:** The dunning pipeline requires no modification; overage invoices are structurally identical to subscription invoices and differ only in their `AiAnalysis` line item. The grace-period gate (draft → issued transition) is a new responsibility that can be implemented as a lightweight `BackgroundService` or as part of a scheduled invoice-issuance job, keeping `DunningService` untouched. Threshold notifications are fire-and-forget with idempotency guards stored in Redis or a lightweight Postgres table, preventing duplicate alerts without introducing distributed locking. Cross-repo impact is nil for this change.
