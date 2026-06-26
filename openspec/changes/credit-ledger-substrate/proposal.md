---
tier: GRANDE
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

Per **ADR-0033**, the prepaid AI-credit feature is one signed append-only ledger (the monthly allowance
becomes a recurring grant), delivered as a 3-change program so the cutover that re-points #4's just-shipped
billing math is split into small, characterization-test-gated steps. This is **change (a) — the
substrate**: it lands the ledger persistence, the O(1) balance projection + atomic guarded-debit primitive,
the shared period helper, and the characterization tests — **all inert** (nothing reads the ledger yet), so
`main` stays byte-identical in behaviour while the foundation and its safety net are in place.

## What Changes

- **Migration 012** creates `ai_credit_ledger` (append-only signed entries) and `tenant_credit_balance`
  (O(1) projection), per ADR-0033's schema (TEXT ids, `SMALLINT` enums, `NUMERIC(18,6)` amounts), idempotent
  (`IF NOT EXISTS`), with the partial unique indexes for subscription-grant `(tenant_id, period_key)` and
  top-up `(tenant_id, external_ref)` idempotency.
- **`ICreditLedgerStore` + Postgres & InMemory twins** with the **atomic primitive**: a grant applies
  unconditionally (ledger INSERT + projection `UPDATE … balance += amount`); a debit is the guarded
  `UPDATE tenant_credit_balance SET balance = balance − @debit … WHERE balance >= @debit` + ledger INSERT in
  **one transaction**, returning a discriminated result (`Posted` / `RejectedInsufficientBalance`).
  Rows-affected 0 ⇒ rejected. Balance read is an O(1) PK lookup of the projection.
- **`BillingPeriod` helper** — extract the UTC year-month `[firstOfMonthUtc, firstOfNextMonthUtc)` boundary
  (today duplicated verbatim in 5 sites) into one `BillingPeriod.Current(IClock)` so quota, meter, invoice,
  and the future grant-mint cannot drift. **No call sites are re-pointed in this change** — the helper is
  introduced and the existing duplicated computations are switched to call it (pure refactor, behaviour
  identical, characterization-test-guarded).
- **Characterization tests** that pin #4's current `CheckQuotaAsync` and `BuildAiCreditLineItemAsync`
  numbers byte-for-byte, so change (b)'s cutover can prove behavioural equivalence for allowance-only
  tenants.

## Capabilities

### New Capabilities

- `ai-credit-ledger`: the persistence substrate — append-only signed ledger, O(1) balance projection,
  atomic guarded grant/debit primitive, and the canonical billing-period helper. No enforcement, metering,
  invoicing, or API behaviour changes (those are changes b and c).

## Impact

- **`Verbara.Platform.Billing`**: new `ICreditLedgerStore`, `CreditLedgerEntry` (sealed class), the
  discriminated debit result, and the `BillingPeriod` helper. `DefaultQuotaEnforcementService` /
  `BillingTypificationCreditMeter` / `DefaultMeteringService` / `AiCreditsEndpoints` switch their private
  `GetCurrentPeriod()` to `BillingPeriod.Current(IClock)` (refactor only).
- **`Storage.Postgres`**: migration 012 + `PostgresCreditLedgerStore` (internal sealed, `NpgsqlExecutor`
  connection+transaction overload for the atomic write; explicit `NpgsqlDbType` on nullable params).
- **`Storage.InMemory`**: `InMemoryCreditLedgerStore` twin (lock-guarded compare-and-decrement; dev/test
  default).
- **DI**: register both store twins (non-generic `AddSingleton<ICreditLedgerStore, …>`); no hosted service,
  no endpoint, no permission in this change.
- **No Sdk / Sdk.Pro changes.** No behaviour change on `main` — the ledger is written by nothing yet.
- **Authoritative design:** ADR-0033.
