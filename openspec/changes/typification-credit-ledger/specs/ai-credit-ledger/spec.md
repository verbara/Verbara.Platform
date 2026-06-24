## ADDED Requirements

### Requirement: Credit ledger aggregate exists per tenant
The system SHALL maintain a `CreditLedger` aggregate per tenant, composed of an ordered sequence of
immutable `CreditLedgerEntry` rows. Each entry SHALL be typed as either a **Grant** (positive amount)
or a **Debit** (negative amount). The running balance at any point in time SHALL equal the sum of all
entry amounts up to that point. The balance SHALL never go below zero as a result of a debit; a debit
that would produce a negative balance SHALL be rejected with a `QuotaExceededException`.

#### Scenario: Grant increases the balance
- **GIVEN** a tenant whose ledger balance is 500 AI Credits
- **WHEN** a Grant entry of 200 AI Credits is persisted
- **THEN** the ledger balance becomes 700 AI Credits

#### Scenario: Debit reduces the balance
- **GIVEN** a tenant whose ledger balance is 300 AI Credits
- **WHEN** a Debit entry of 100 AI Credits is posted (referencing a `UsageRecord.RecordId`)
- **THEN** the ledger balance becomes 200 AI Credits

#### Scenario: Debit rejected when balance insufficient
- **GIVEN** a tenant whose ledger balance is 50 AI Credits
- **WHEN** a Debit of 100 AI Credits is attempted
- **THEN** the system SHALL reject the operation and return a quota-exceeded error; the ledger balance SHALL remain 50 AI Credits

### Requirement: Ledger entries are immutable
Once a `CreditLedgerEntry` is persisted it SHALL NOT be modified or deleted. Corrections SHALL be
expressed as offsetting Grant entries (for reversal of a debit) or offsetting Debit entries (for
clawback of a grant), each with a `reason` string and an optional `correlationId` referencing the
original entry.

#### Scenario: Reversal of an erroneous debit
- **GIVEN** a Debit entry `D-42` was posted in error
- **WHEN** an operator issues a reversal Grant of the same amount referencing `correlationId = "D-42"` with reason `"reversal"`
- **THEN** the ledger shows both the original Debit and the reversal Grant; the net balance is restored; `D-42` is NOT deleted

### Requirement: Top-up purchase API
The system SHALL expose a `POST /api/v1/billing/credit-ledger/top-up` endpoint (permission:
`billing:credit-ledger:write`) that records a Grant entry for the calling tenant. The request body
SHALL include `amountCredits` (positive integer), `reason` (one of `purchase`, `promotion`,
`partner_allocation`, `reversal`), and an optional `externalTransactionId` (idempotency key). The
endpoint SHALL be idempotent on `externalTransactionId`: a duplicate call with the same key SHALL
return the existing grant entry without double-posting.

#### Scenario: Successful top-up
- **GIVEN** an authenticated tenant admin with `billing:credit-ledger:write` permission
- **WHEN** `POST /api/v1/billing/credit-ledger/top-up` is called with `{ "amountCredits": 1000, "reason": "purchase", "externalTransactionId": "txn-abc-123" }`
- **THEN** a Grant entry of 1000 credits is persisted; the response body contains the new balance; HTTP 201 is returned

#### Scenario: Duplicate top-up is idempotent
- **GIVEN** a Grant with `externalTransactionId = "txn-abc-123"` already exists
- **WHEN** `POST /api/v1/billing/credit-ledger/top-up` is called again with the same `externalTransactionId`
- **THEN** HTTP 200 is returned with the existing entry; no new Grant row is inserted; balance is unchanged

#### Scenario: Unauthorized top-up is rejected
- **GIVEN** an authenticated agent without `billing:credit-ledger:write` permission
- **WHEN** `POST /api/v1/billing/credit-ledger/top-up` is called
- **THEN** HTTP 403 is returned; no ledger entry is created

### Requirement: Balance query API
The system SHALL expose a `GET /api/v1/billing/credit-ledger/balance` endpoint (permission:
`billing:credit-ledger:read`) returning the current balance, total granted, total debited, and the
timestamp of the last entry for the calling tenant.

#### Scenario: Balance query for tenant with ledger
- **GIVEN** a tenant with a ledger containing multiple grants and debits
- **WHEN** `GET /api/v1/billing/credit-ledger/balance` is called
- **THEN** HTTP 200 is returned with `{ "balance": <current>, "totalGranted": <sum grants>, "totalDebited": <sum debits>, "lastEntryAt": <ISO-8601> }`

#### Scenario: Balance query for tenant without ledger
- **GIVEN** a tenant that has never had a credit ledger entry
- **WHEN** `GET /api/v1/billing/credit-ledger/balance` is called
- **THEN** HTTP 200 is returned with `{ "balance": 0, "totalGranted": 0, "totalDebited": 0, "lastEntryAt": null }`

### Requirement: Ledger entry history API
The system SHALL expose a `GET /api/v1/billing/credit-ledger/entries` endpoint (permission:
`billing:credit-ledger:read`) returning a paginated list of ledger entries in descending
chronological order, with optional filters for `type` (grant / debit), `from`, and `to` date ranges.
Page size SHALL be capped at 200 entries per request.

#### Scenario: Paginated entry list
- **GIVEN** a tenant with 500 ledger entries
- **WHEN** `GET /api/v1/billing/credit-ledger/entries?pageSize=50&page=2` is called
- **THEN** HTTP 200 is returned with entries 51-100 in descending order; response includes `totalCount` and `hasMore` fields

### Requirement: Quota enforcement reads the ledger
`IQuotaEnforcementService.CheckQuotaAsync` SHALL consult the ledger balance for `UsageType.AiAnalysis`
when the tenant has at least one Grant entry in its credit ledger, instead of the `TenantQuota.AiCreditsMonthly`
scalar. If both a ledger balance and a monthly allowance exist, the ledger balance SHALL take precedence.
A tenant whose ledger balance is zero SHALL be treated as `QuotaAction.HardBlock` for AI usage regardless
of the monthly allowance setting.

#### Scenario: Quota check passes when ledger balance is sufficient
- **GIVEN** a tenant with a ledger balance of 100 AI Credits
- **WHEN** `CheckQuotaAsync(tenantId, UsageType.AiAnalysis, 10)` is called
- **THEN** `QuotaCheckResult.Allowed = true` is returned; `UsagePercent` reflects 10% consumed

#### Scenario: Quota check blocks when ledger balance is zero
- **GIVEN** a tenant with a ledger balance of 0 AI Credits and `AiCreditsMonthly = 1000`
- **WHEN** `CheckQuotaAsync(tenantId, UsageType.AiAnalysis, 1)` is called
- **THEN** `QuotaCheckResult.Allowed = false` is returned with reason `"ai_credit_ledger_exhausted"`

#### Scenario: Monthly allowance used when no ledger exists
- **GIVEN** a tenant with no ledger entries and `AiCreditsMonthly = 500`
- **WHEN** `CheckQuotaAsync(tenantId, UsageType.AiAnalysis, 50)` is called
- **THEN** the existing monthly-allowance logic is applied unchanged; ledger is not consulted

### Requirement: Metering posts debit entries
`IMeteringService.RecordUsageAsync` SHALL atomically persist the `UsageRecord` and a corresponding
Debit `CreditLedgerEntry` referencing `UsageRecord.RecordId` when recording a `UsageType.AiAnalysis`
event for a tenant that has a credit ledger. The operation SHALL use a single Postgres transaction;
if the debit would underflow the balance the entire transaction SHALL be rolled back and a
`QuotaExceededException` thrown to the caller.

#### Scenario: Metering atomically posts debit
- **GIVEN** a tenant with a ledger balance of 200 AI Credits
- **WHEN** `RecordUsageAsync(tenantId, UsageType.AiAnalysis, 30, ...)` is called
- **THEN** a `UsageRecord` row and a Debit entry of 30 credits are committed in the same transaction; balance becomes 170

#### Scenario: Metering rolls back on underflow
- **GIVEN** a tenant with a ledger balance of 10 AI Credits
- **WHEN** `RecordUsageAsync(tenantId, UsageType.AiAnalysis, 50, ...)` is called
- **THEN** no `UsageRecord` row is persisted; no Debit entry is created; `QuotaExceededException` is thrown; balance remains 10

### Requirement: credit_ledger Postgres migration
The system SHALL include an idempotent migration that creates the `credit_ledger` table with columns:
`entry_id` (uuid PK), `tenant_id` (uuid NOT NULL), `entry_type` (varchar: `grant`/`debit`), `amount_credits`
(bigint NOT NULL), `reason` (varchar), `reference_record_id` (uuid NULLABLE — FK to `usage_records`),
`correlation_id` (uuid NULLABLE), `external_transaction_id` (varchar NULLABLE UNIQUE per tenant),
`created_at` (timestamptz NOT NULL DEFAULT now()). A partial unique index on `(tenant_id, external_transaction_id)`
WHERE `external_transaction_id IS NOT NULL` SHALL enforce top-up idempotency at the database level.

#### Scenario: Migration is idempotent
- **GIVEN** the migration has already run on the target database
- **WHEN** the migration is applied again
- **THEN** no error is raised; no duplicate tables or indexes are created

#### Scenario: Duplicate externalTransactionId is rejected at the DB level
- **GIVEN** a Grant with `external_transaction_id = "txn-xyz"` exists for tenant T
- **WHEN** an INSERT with the same `(tenant_id, external_transaction_id)` is attempted
- **THEN** the database raises a unique constraint violation; no new row is inserted

### Requirement: Ledger reconciliation with invoicing
Each `CreditLedgerEntry` of type Debit SHALL carry a `reference_record_id` pointing to the originating
`UsageRecord`. Invoice generation (`IInvoiceGenerationService`) SHALL, when summing AI usage charges,
first subtract the total AI Credits consumed from the ledger balance to derive the post-paid remainder.
Only the remainder SHALL be billed at the rate-card price.

#### Scenario: Invoice subtracts prepaid credits
- **GIVEN** a tenant consumed 1000 AI Credits in a period and had 600 credits prepaid (ledger debits)
- **WHEN** the monthly invoice is generated
- **THEN** only 400 credits appear as a chargeable line item at the rate-card price; the invoice includes a credit-consumption summary line showing 600 prepaid credits used

### Requirement: Web balance display
The Platform Web billing panel SHALL display the tenant's current AI Credit balance, total granted,
total debited, and a link to the ledger entry history. The balance SHALL be fetched from
`GET /api/v1/billing/credit-ledger/balance` on page load.

#### Scenario: Balance widget renders for tenant with credits
- **GIVEN** a tenant admin views the billing settings page
- **WHEN** the page loads and the balance API returns `{ "balance": 350, "totalGranted": 500, "totalDebited": 150 }`
- **THEN** the widget displays "350 AI Credits remaining" with granted/debited breakdown visible

#### Scenario: Balance widget shows zero state
- **GIVEN** a tenant admin views the billing settings page and the tenant has no ledger
- **WHEN** the page loads
- **THEN** the widget displays "0 AI Credits" with a call-to-action linking to the top-up flow

### Requirement: AOT compatibility
All new types introduced by the credit-ledger feature SHALL be AOT-compatible (request/response DTOs,
domain classes, enum values). Every DTO used in HTTP request/response bodies SHALL be registered
in `ApiJsonContext` via `[JsonSerializable]`. No reflection, `Activator.CreateInstance`, or dynamic
proxies SHALL be used anywhere in the ledger implementation.

#### Scenario: AOT publish succeeds with ledger types
- **GIVEN** the Platform.Api project has been updated with all ledger DTOs registered in `ApiJsonContext`
- **WHEN** `dotnet publish -r linux-x64 -c Release` is executed
- **THEN** the publish succeeds with zero `IL2026`/`IL3050`/`IL207x` diagnostics and no managed Verbara DLLs in the output

## Architectural Risk

**Level:** MEDIUM

**Affected:**
- `Verbara.Platform.Billing` — new aggregate + store interface + mutation path in metering; existing `DefaultQuotaEnforcementService` and `DefaultMeteringService` gain ledger-conditional logic
- `Storage.Postgres` — new table + migration; Npgsql query paths must be validated for AOT
- `Verbara.Platform.Api` — two new endpoint groups; new permission strings must be seeded in `RoleTemplateSeeder`
- `Verbara.Platform.Web` — new balance widget; must handle network errors gracefully (stale balance not blocking UI)

**Mitigation:**
- Ledger logic is **additive and conditional**: tenants without a ledger follow existing monthly-allowance path unchanged (no behavioural regression for existing tenants)
- Atomic `UsageRecord + Debit` write uses a single Npgsql transaction with explicit `NpgsqlDbType` on all parameters (prevents `42P08` ambiguous type errors per ADR-0022 data-access rules)
- Idempotency enforced at DB level (partial unique index) — top-up endpoint cannot double-post even under concurrent retries
- Payment provider integration is explicitly deferred — the ledger accepts pre-authorised amounts only; no payment-rail code ships in this change
