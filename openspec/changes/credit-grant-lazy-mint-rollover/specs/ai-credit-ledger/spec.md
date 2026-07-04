# ai-credit-ledger — Delta

## ADDED Requirements

### Requirement: Lazy mint on the first balance read of a new period
The system SHALL mint the current-period `Subscription` grant inline — before computing the returned
balance — when a balance read on the enforcement/readout path occurs for a tenant whose
`TenantQuota.AiCreditsMonthly` is non-null and no current-period `Subscription` grant exists, using
the existing idempotent posting (`(tenant_id, period_key, entry_type)` `ON CONFLICT DO NOTHING` +
conditional projection upsert). The scheduled mint worker remains the steady-state mint; the lazy
mint exists only to close the ≤ `DunningConfig.CheckIntervalHours` window after a UTC month
rollover. A balance read for a tenant whose current-period grant already exists SHALL NOT perform
any write.

#### Scenario: First read after rollover mints the grant
- **GIVEN** a tenant with `AiCreditsMonthly = 1000`, a new UTC month has started, and the mint worker has not ticked yet
- **WHEN** the tenant's balance is read by the quota pre-check
- **THEN** the current-period `Subscription` grant of 1000 is minted inline and the returned balance includes it

#### Scenario: Concurrent first reads mint exactly once
- **GIVEN** two concurrent balance reads for the same tenant in the rollover window
- **WHEN** both attempt the lazy mint
- **THEN** exactly one grant row exists for the period and the projection is credited exactly once

#### Scenario: Worker tick after a lazy mint is a no-op
- **GIVEN** a tenant whose current-period grant was lazy-minted
- **WHEN** the mint worker's next cycle runs
- **THEN** it neither double-inserts nor double-credits (idempotent posting)

#### Scenario: Steady-state reads perform no writes
- **GIVEN** a tenant whose current-period grant already exists
- **WHEN** its balance is read
- **THEN** no ledger write occurs (read stays O(1))
