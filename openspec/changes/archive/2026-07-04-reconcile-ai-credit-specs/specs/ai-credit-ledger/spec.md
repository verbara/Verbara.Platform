# ai-credit-ledger — Delta

## ADDED Requirements

### Requirement: Ledger is the single owner of the quota decision and invoice-overage computation
When the **enforcement** feature flag is ON, the quota decision for `UsageType.AiAnalysis` SHALL be
produced exclusively by this capability — the O(1) `tenant_credit_balance` projection plus the
`QuotaOutcome` mapping specified here. When the **invoice-read** feature flag is ON, the customer-owed
AiAnalysis overage SHALL be computed exclusively as `Σ |PostPaid debits|` per this spec. The legacy
accounts — the `UsageRecord`-token-sum quota pre-check in `typification-platform-llm` and the
allowance-based usage-record overage in `ai-credit-billing` — SHALL apply only while the respective
flag is OFF, and SHALL defer to this spec once it is ON. The differentiated-credit computation in
`ai-credit-metering` SHALL remain the pricing basis (tokens → credits) consumed by both paths; it
SHALL NOT own the enforcement outcome.

#### Scenario: Enforcement flag ON — the ledger decides quota
- **GIVEN** the enforcement flag is ON for a `PlatformManaged` tenant
- **WHEN** an AiAnalysis quota pre-check runs
- **THEN** the outcome derives from the projection balance and the `QuotaOutcome` requirement of this spec, and no `usage_records` token sum decides it

#### Scenario: Flags OFF — the legacy specs govern unchanged
- **GIVEN** the enforcement and invoice-read flags are both OFF
- **WHEN** a quota pre-check runs and an invoice is generated
- **THEN** the legacy paths specified in `typification-platform-llm` and `ai-credit-billing` run unchanged and the ledger is not consulted for either outcome

### Requirement: Characterization baseline pins the legacy money path
Characterization tests SHALL pin the legacy `CheckQuotaAsync` and `BuildAiCreditLineItemAsync`
outputs (the change-(a) baseline) byte-for-byte for representative consumed/allowance inputs, so
every flag-gated cutover step can prove flag-OFF equivalence and allowance-only flag-ON equivalence
against a fixed reference.

#### Scenario: Flag-OFF behaviour equals the pinned baseline
- **GIVEN** the enforcement and invoice-read flags are OFF
- **WHEN** the characterization suite runs
- **THEN** quota decisions and invoice amounts SHALL equal the pinned change-(a) values byte-for-byte

## MODIFIED Requirements

### Requirement: Metered consumption is a two-step covered-plus-PostPaid debit
The ledger SHALL expose a metered-debit primitive `PostMeteredDebitAsync(tenantId, debit, usageRecordId, ct)`
that, in a **single transaction**, draws the covered portion `covered = min(available, debit)` from the
tenant's open prepaid stock — allocated across lots per the "Per-grant lots with a provably-total FIFO
multi-source allocation order" requirement, which owns the allocation detail — via the guarded projection
update (the projection SHALL floor at 0; prepaid stock is never overdrawn), recording one **source-tagged**
covered debit row per drawn lot, and SHALL post any uncovered remainder `tail = debit − covered` as exactly
one **unconditional** debit row with `source = PostPaid` that does **not** modify the projection. The result
SHALL report the new projection balance, the covered amount, and the PostPaid amount.

#### Scenario: Debit fully covered by prepaid balance
- **GIVEN** a tenant with a single 10-credit `Subscription` lot (projection balance 10) and an incoming debit of 4 credits
- **WHEN** `PostMeteredDebitAsync(tenant, 4, …)` runs
- **THEN** the projection balance becomes 6, one debit row of −4 tagged `Subscription` (the drawn lot's source) is written, and the result reports covered 4 / postPaid 0

#### Scenario: Debit overflows into PostPaid tail
- **GIVEN** a tenant with 3 credits of open prepaid stock and an incoming debit of 5 credits
- **WHEN** `PostMeteredDebitAsync(tenant, 5, …)` runs
- **THEN** the projection balance becomes 0, a −3 covered debit tagged with the drawn lot's source and a single −2 `PostPaid` debit are written, and the result reports covered 3 / postPaid 2

#### Scenario: Concurrent metered debits never overdraw the prepaid stock
- **GIVEN** two concurrent `PostMeteredDebitAsync` calls of 3 credits each against 4 credits of open prepaid stock
- **WHEN** both commit
- **THEN** the projection balance is 0 (never negative), the total covered across both is exactly 4, and the remaining 2 credits are recorded as `PostPaid` tail

### Requirement: Credit-grant and credit-read permissions
The system SHALL define permissions `billing:credits:grant` and `billing:credits:read`. `billing:credits:read`
SHALL be granted to the operator (`platform_admin`) and tenant-admin role templates (it permits reading one's
own balance). `billing:credits:grant` SHALL be granted to the operator (`platform_admin`) role template
**only** — it SHALL NOT be granted to tenant `admin`/`system_admin` (so it must NOT be added to
`AllPermissions()`), **nor to `partner_admin`**: per the c2 resolution, Promo and Partner grants are
operator-minted (see "Operator-minted Promo and Partner grants") and no partner-facing grant permission
exists. A partner-scoped self-service top-up (with owning-child validation) is an explicit **deferred
follow-up**, not a committed forward promise; it would introduce the partner grant surface only when that
change ships. Existing tenants receive `:read` on `platform_admin` via the `RbacReseed` CLI.

#### Scenario: Tenant admin cannot mint credits
- **GIVEN** a tenant `admin` (not platform/partner) without `billing:credits:grant`
- **WHEN** `POST …/credit-ledger/top-up` is called
- **THEN** HTTP 403 is returned

#### Scenario: Partner admin cannot mint credits
- **GIVEN** a `partner_admin` caller (which does not hold `billing:credits:grant`)
- **WHEN** any credit-grant endpoint is called
- **THEN** HTTP 403 is returned and no grant is created

## REMOVED Requirements

### Requirement: Substrate is inert (no behaviour change)
**Reason**: Transitional change-(a) requirement, falsified once the cutover shipped — the mint worker
(`CreditGrantMintWorker`, registered unconditionally in `Program.cs`) writes grants at runtime and the
meter debits the ledger behind the enforcement flag, directly contradicting "Nothing reads or writes
the ledger at runtime yet" and the empty-ledger scenario.
**Migration**: The flag-gated runtime account lives in "Enforcement and metering read the ledger behind
a cutover flag"; the requirement's durable content (the pinned baseline) is re-added as
"Characterization baseline pins the legacy money path".
