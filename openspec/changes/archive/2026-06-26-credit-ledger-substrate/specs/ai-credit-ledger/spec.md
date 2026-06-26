## ADDED Requirements

### Requirement: Append-only signed credit ledger
The system SHALL persist AI credits as an append-only `ai_credit_ledger` of immutable signed entries. Each
entry SHALL carry `entry_type` (Grant/Debit) as a `SMALLINT` ordinal, a `source` (Subscription, TopUp,
Promo, Partner, PostPaid) as a `SMALLINT` ordinal, a signed `amount NUMERIC(18,6)` (grants positive, debits
negative), an optional `period_key` (`"yyyy-MM"` UTC), an optional `external_ref`, an optional `expires_at`,
an optional `usage_record_id` back-reference, and `created_at`. Identifiers SHALL be `TEXT` (EntityId hex),
matching the schema convention. Entries SHALL never be updated or deleted; corrections are offsetting
entries.

#### Scenario: Ledger entry is immutable and append-only
- **GIVEN** a persisted `CreditLedgerEntry`
- **WHEN** any correction is required
- **THEN** the system SHALL append an offsetting entry; the original entry SHALL remain unchanged

#### Scenario: Ledger SUM equals the projection balance
- **GIVEN** a tenant with any sequence of grant and debit entries
- **WHEN** the ledger `SUM(amount)` (over live, non-expired entries) is computed offline
- **THEN** it SHALL equal the tenant's `tenant_credit_balance.balance`

### Requirement: O(1) balance projection with atomic guarded debit
The system SHALL maintain a `tenant_credit_balance` projection row `(tenant_id PK, balance NUMERIC(18,6),
version BIGINT, updated_at)` updated in the **same transaction** as every ledger entry. A grant SHALL apply
unconditionally (`balance += amount`). A debit SHALL be applied by a single guarded statement
`UPDATE tenant_credit_balance SET balance = balance − @debit, version = version + 1 WHERE tenant_id = @t AND
balance >= @debit`; the ledger debit row SHALL be inserted in the same transaction. The balance read used on
the request path SHALL be an O(1) primary-key lookup of the projection, never a `SUM` aggregate over the
ledger.

#### Scenario: Debit within balance is posted atomically
- **GIVEN** a tenant whose projection balance is 300 credits
- **WHEN** a debit of 100 credits is posted
- **THEN** the ledger gains a −100 debit entry, the projection balance becomes 200, both in one transaction, and the store returns `Posted`

#### Scenario: Debit exceeding balance is rejected, nothing is written
- **GIVEN** a tenant whose projection balance is 50 credits
- **WHEN** a debit of 100 credits is attempted
- **THEN** the guarded `UPDATE` affects 0 rows, no ledger entry is written, the balance remains 50, and the store returns `RejectedInsufficientBalance`

#### Scenario: Concurrent debits cannot drive the balance negative
- **GIVEN** a tenant whose projection balance is 5 credits and two concurrent debits of 5 credits each
- **WHEN** both are posted concurrently
- **THEN** exactly one SHALL return `Posted` (balance 0) and the other `RejectedInsufficientBalance`; the balance SHALL never become negative

### Requirement: Idempotent grant posting
A grant carrying a `period_key` (subscription) SHALL be idempotent on `(tenant_id, period_key, entry_type)`
via a partial unique index and `INSERT … ON CONFLICT DO NOTHING`; a grant carrying an `external_ref`
(top-up) SHALL be idempotent on `(tenant_id, external_ref)`. A duplicate grant SHALL be a no-op that neither
double-inserts nor double-credits the projection.

#### Scenario: Duplicate subscription grant for the same period is a no-op
- **GIVEN** a Subscription grant for `(tenant T, period "2026-06")` already exists
- **WHEN** the same `(tenant, period)` grant is posted again
- **THEN** no second ledger row is inserted and the projection balance is unchanged

### Requirement: Canonical billing-period helper
The system SHALL expose a single `BillingPeriod.Current(IClock)` returning the UTC calendar-month boundary
`[firstOfMonthUtc, firstOfNextMonthUtc)` and the `"yyyy-MM"` period key. All sites that currently compute
the month boundary (quota enforcement, the credit meter, the credits readout, metering summary) SHALL call
this helper. This change SHALL NOT alter any computed boundary — it only removes duplication.

#### Scenario: Helper matches the existing UTC boundary
- **GIVEN** `IClock.UtcNow` = `2026-06-15T12:00:00Z`
- **WHEN** `BillingPeriod.Current(clock)` is evaluated
- **THEN** it SHALL return start `2026-06-01T00:00:00Z`, end `2026-07-01T00:00:00Z`, key `"2026-06"` — identical to the previously inlined computation

### Requirement: Substrate is inert (no behaviour change)
This change SHALL NOT cause any quota decision, metered debit, invoice amount, or API response to differ
from current `main`. Nothing reads or writes the ledger at runtime yet; the store and projection exist and
are unit-tested in isolation. Characterization tests SHALL pin the current `CheckQuotaAsync` and
`BuildAiCreditLineItemAsync` outputs byte-for-byte so the subsequent cutover (change b) can prove
equivalence.

#### Scenario: No runtime path writes the ledger in this change
- **GIVEN** the substrate is deployed
- **WHEN** an AI classification is metered and an invoice is generated
- **THEN** the results SHALL be identical to pre-deployment `main`, and `ai_credit_ledger` SHALL remain empty for that tenant

## Architectural Risk

**Level:** LOW (this change), MEDIUM (program — see ADR-0033)

**Affected:** `Verbara.Platform.Billing` (new store interface + period helper + the `GetCurrentPeriod`
refactor), `Storage.Postgres` (migration 012 + store), `Storage.InMemory` (store twin), DI registration.

**Mitigation:** The change is inert — no enforcement/metering/invoice/API path reads the ledger, so there is
no behavioural surface to regress. The only edit to existing code is replacing four identical inlined
`GetCurrentPeriod()` computations with one shared helper, guarded by characterization tests that assert the
boundary is unchanged. The migration is additive and idempotent. The atomic primitive is exercised by unit
tests (including the concurrent-debit race) on both store twins.
