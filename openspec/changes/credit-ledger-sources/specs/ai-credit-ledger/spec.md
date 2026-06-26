## ADDED Requirements

### Requirement: Prepaid, promo and partner grant sources
The system SHALL support `TopUp`, `Promo`, and `Partner` grant sources in addition to `Subscription`.
`TopUp` grants SHALL be idempotent on `external_ref`. `Promo` grants MAY carry an `expires_at`. `Partner`
grants SHALL be attributable to the partner-revenue ledger and SHALL NOT be billed on the customer invoice.

#### Scenario: Idempotent top-up
- **GIVEN** a `TopUp` grant with `external_ref = "txn-abc"` already exists for tenant T
- **WHEN** the same `external_ref` top-up is posted again
- **THEN** no second grant is inserted and the balance is unchanged

### Requirement: Multi-source consumption and invoicing
When two or more consumable sources coexist, each debit SHALL be allocated FIFO over open lots ordered by
`(billable_priority, expires_at, created_at)`; the uncovered tail SHALL be a `PostPaid` lot. Invoice
customer-owed credits SHALL equal the sum of allocations drawn from `PostPaid` lots within the period;
Subscription/Prepaid/Promo/Partner draws SHALL never be re-billed nor cross-attributed.

#### Scenario: Partner credit is not billed to the customer
- **GIVEN** a tenant with a 1000-credit `Partner` grant who consumes 600 credits
- **WHEN** the invoice is generated
- **THEN** the customer-owed AiAnalysis amount SHALL be 0 and the 600 partner-funded credits SHALL appear on the partner-revenue ledger, not the customer line item

### Requirement: Top-up and balance API with RBAC
The system SHALL expose `POST …/credit-ledger/top-up` (permission `billing:credits:grant`),
`GET …/credit-ledger/balance` and `GET …/credit-ledger/entries` (permission `billing:credits:read`,
paginated via `Core.PagedResult<T>`). `billing:credits:grant` SHALL be granted to operator/partner role
templates only in this iteration.

#### Scenario: Unauthorised top-up is rejected
- **GIVEN** a caller without `billing:credits:grant`
- **WHEN** `POST …/credit-ledger/top-up` is called
- **THEN** HTTP 403 is returned and no grant is created
