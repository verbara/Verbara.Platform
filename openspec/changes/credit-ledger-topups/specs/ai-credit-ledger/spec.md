## ADDED Requirements

### Requirement: Operator-minted top-up grants
The system SHALL allow an operator or partner with permission `billing:credits:grant` to mint a `TopUp` grant
for a tenant via `POST /api/v1/management/credit-ledger/top-up`. The grant SHALL be a positive-amount
`CreditEntryType.Grant` with `CreditSource.TopUp`, idempotent on a caller-supplied idempotency key carried as
`external_ref` (a repeated key SHALL NOT mint a second grant). A caller without `billing:credits:grant` SHALL
receive HTTP 403 and no grant SHALL be created. A non-positive amount SHALL be rejected with HTTP 400.

#### Scenario: Top-up adds fungible balance
- **GIVEN** a tenant with balance 200 credits
- **WHEN** an operator posts a top-up of 500 with idempotency key `txn-abc`
- **THEN** the tenant balance becomes 700 and a `TopUp` grant of 500 with `external_ref = "txn-abc"` exists

#### Scenario: Idempotent top-up
- **GIVEN** a `TopUp` grant with `external_ref = "txn-abc"` already exists for the tenant
- **WHEN** the same idempotency key is posted again
- **THEN** no second grant is inserted and the balance is unchanged

#### Scenario: Unauthorised top-up is rejected
- **GIVEN** a caller without `billing:credits:grant`
- **WHEN** `POST …/credit-ledger/top-up` is called
- **THEN** HTTP 403 is returned and no grant is created

### Requirement: Top-up consumption is correct without lot machinery
A `TopUp` grant SHALL be fungible — consumed by the existing metered-debit covered draw exactly like a
`Subscription` grant — and SHALL NOT change the customer invoice computation. For an allowance-plus-top-up
tenant the invoiced overage SHALL remain `Σ |PostPaid debits|`, and `PostMeteredDebitAsync` SHALL be unchanged
by this change.

#### Scenario: Top-up reduces billable overage
- **GIVEN** a tenant with `AiCreditsMonthly = 1000` who has a 500 `TopUp` and consumes 1200 credits in the period
- **WHEN** the invoice is generated
- **THEN** the `PostPaid` tail (billable overage) is 0 (1000 subscription + 500 top-up cover the 1200) and the customer-owed amount is 0

### Requirement: Tenant balance and entries read API
The system SHALL expose `GET /api/v1/admin/credit-ledger/balance` (the current O(1) projection balance) and
`GET /api/v1/admin/credit-ledger/entries` (paginated, `Core.PagedResult<CreditLedgerEntryDto>`), both requiring
permission `billing:credits:read`, scoped to the calling tenant. The entries response SHALL carry an accurate
`TotalCount` (served by a new `ICreditLedgerStore.GetEntriesCountAsync`). Entry ordering SHALL be
most-recent-first and deterministic across the Postgres and InMemory stores (a stable tiebreak on `entry_id`).

#### Scenario: Tenant reads its own balance
- **GIVEN** a tenant admin with `billing:credits:read` whose balance is 700
- **WHEN** `GET …/credit-ledger/balance` is called
- **THEN** HTTP 200 returns the balance 700 for the calling tenant only

#### Scenario: Paginated entries carry a total count
- **GIVEN** a tenant with 30 ledger entries
- **WHEN** `GET …/credit-ledger/entries?page=1&pageSize=25` is called
- **THEN** the `PagedResult` returns the 25 most-recent entries with `TotalCount = 30` and `TotalPages = 2`

### Requirement: Credit-grant and credit-read permissions
The system SHALL define permissions `billing:credits:grant` and `billing:credits:read`. `billing:credits:read`
SHALL be granted to the operator (`platform_admin`) and tenant-admin role templates (it permits reading one's
own balance). `billing:credits:grant` SHALL be granted to operator (`platform_admin`) and partner
(`partner_admin`) role templates **only** — it SHALL NOT be granted to tenant `admin`/`system_admin`. Existing
tenants receive `:read` on `platform_admin` via the `RbacReseed` CLI; partner-role propagation to existing
tenants is out of scope (fresh provisioning only).

#### Scenario: Tenant admin cannot mint credits
- **GIVEN** a tenant `admin` (not platform/partner) without `billing:credits:grant`
- **WHEN** `POST …/credit-ledger/top-up` is called
- **THEN** HTTP 403 is returned
