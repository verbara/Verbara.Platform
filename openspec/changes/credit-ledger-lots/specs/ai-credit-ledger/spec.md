## ADDED Requirements

### Requirement: Per-grant lots with FIFO multi-source allocation
The system SHALL track a mutable `remaining` per grant lot and SHALL allocate each metered debit FIFO across
open, non-expired lots in the draw order `Promo (soonest-expiring) → Partner → Subscription/TopUp → PostPaid`,
recording the debit→lot linkage. The locking selection SHALL order lots deterministically to avoid deadlock,
and no lot SHALL be overdrawn under concurrency. The invoice customer-owed SHALL remain `Σ |PostPaid debits|`,
and the change-(a)/(b) characterization values SHALL hold for a single-Subscription tenant (n=1 lots).

#### Scenario: Draw spans promo then subscription then PostPaid
- **GIVEN** a tenant with a 100-credit Promo lot and a 1000-credit Subscription lot who consumes 1150 credits
- **THEN** 100 is drawn from Promo, 1000 from Subscription, and 50 is the PostPaid (billable) tail

### Requirement: Promo expiry reclaims unconsumed credits
`Promo` grants MAY carry `expires_at`. Expired lots SHALL NOT be FIFO-selected, and a reclaim step SHALL post an
offsetting append-only debit so expired unconsumed remaining leaves the balance.

#### Scenario: Unconsumed promo expires
- **GIVEN** a 100-credit Promo lot expiring on day 15, 30 consumed before expiry
- **WHEN** the reclaim runs after day 15
- **THEN** the remaining 70 is removed from the balance via an offsetting Promo debit and is no longer spendable

### Requirement: Partner-funded credits are attributed, never customer-billed
`Partner` draws SHALL never appear in the customer invoice and SHALL be attributed to the owning partner —
resolved via `Tenant.ParentTenantId` gated on `parent.Type == TenantType.Partner` — in a non-invoice-keyed
`partner_credit_allocation` record `(partner_tenant_id, customer_tenant_id, period_key)`, written at period
close independent of customer-invoice generation.

#### Scenario: Partner credit is attributed without a customer invoice
- **GIVEN** a tenant with a 1000-credit Partner grant who consumes 600 credits and has no customer overage
- **WHEN** the period closes
- **THEN** the customer-owed amount is 0 and a `partner_credit_allocation` of 600 exists for the owning partner
