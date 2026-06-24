---
tier: GRANDE
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

P2c.2 ships monthly-allowance + post-paid metering (Approach A), which covers recurring subscription
plans but cannot support prepaid bundles, one-time top-ups, or the gifting of credits as promotional
grants. Without a first-class credit ledger, prepaid scenarios require ad-hoc workarounds against the
monthly quota fields and produce no auditable balance trail — a gap that blocks enterprise and partner
self-service billing models.

## What Changes

- **New aggregate `CreditLedger`** (grants + debits + running balance) added to `Verbara.Platform.Billing`; replaces the flat `AiCreditsMonthly` scalar for prepaid tenants.
- **Top-up / credit-purchase flow**: new API endpoints to record a credit grant (top-up purchase, promotional grant, or partner allocation); credit grants are immutable once persisted.
- **Quota + metering read the ledger balance** in addition to (or instead of) the monthly allowance: `IQuotaEnforcementService.CheckQuotaAsync` for `UsageType.AiAnalysis` checks the ledger balance when a ledger exists for the tenant; `IMeteringService.RecordUsageAsync` posts a corresponding debit entry.
- **`credit_ledger` migration**: new Postgres table storing ledger entries (grant/debit rows) keyed on `(tenant_id, entry_id)` with a partial index on open balances.
- **Reconciliation with `UsageRecord`/invoicing**: debit entries reference the originating `UsageRecord.RecordId` so invoices can reconcile credits consumed vs. post-paid remainder.
- **Web balance + purchase UI**: balance display in the tenant billing panel; top-up purchase flow (payment provider integration deferred to a follow-on task).

## Capabilities

### New Capabilities

- `ai-credit-ledger`: Prepaid AI-Credit ledger — grants, debits, running balance, top-up API, quota integration, and reconciliation with UsageRecords.

### Modified Capabilities

<!-- No existing spec-level capabilities are changed by this proposal. The quota and metering
     interfaces gain new behaviour conditional on ledger presence, but no existing requirement
     text is removed — those additions appear under ADDED Requirements in the spec delta. -->

## Impact

- **`Verbara.Platform.Billing`** (primary): new `ICreditLedgerStore`, `ICreditLedgerService`, `CreditLedger` aggregate, `CreditLedgerEntry` (discriminated grant/debit), migration.
- **`Verbara.Platform.Api`**: new `/billing/credit-ledger` endpoint group; `IQuotaEnforcementService` + `IMeteringService` updated to consult ledger; DTOs registered in `ApiJsonContext`.
- **`Storage.Postgres`**: new `credit_ledger` table + migration; updated `DefaultQuotaEnforcementService` + `DefaultMeteringService`.
- **`Verbara.Platform.Web`** (Platform.Web repo): balance widget + top-up purchase panel in billing settings.
- **No Sdk / Sdk.Pro changes required** — credit ledger is wholly contained in Platform + Platform.Web.
- **Payment provider integration** (Stripe / MercadoPago): deferred; the top-up endpoint accepts pre-authorised grant amounts; the caller (webhook / admin) is responsible for payment confirmation in this iteration.
