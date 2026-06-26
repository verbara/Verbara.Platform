---
tier: GRANDE
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

Change **(b)** of the AI-credit-ledger program (ADR-0033). With the substrate (change a) landed inert, this
change performs the **cutover**: it re-points #4's three billing seams onto the ledger and turns the monthly
`AiCreditsMonthly` allowance into a recurring grant — so there is one source of truth for "credits left".
Behaviour stays **byte-identical for allowance-only tenants**, proven by the characterization tests landed
in change (a). This is the highest-regression-risk step in the program and is deliberately isolated.

> Full spec + tasks are authored at the start of this change (after change a merges), re-grounded against
> the then-current code. This proposal fixes scope and the load-bearing decisions per ADR-0033.

## What Changes

- **Quota** — `DefaultQuotaEnforcementService.CheckQuotaAsync` (AiAnalysis) reads the O(1) projection balance
  instead of recomputing from `usage_records`; add `QuotaOutcome { Allow, Warn, SoftBlock, HardBlock }` to
  `QuotaCheckResult`; enforcement becomes the sole authority (balance ≤ debit ⇒ `HardBlock`); the classify
  endpoint switches on `Outcome` and drops the `GetQuotaStatusAsync` re-read.
- **Metering** — `BillingTypificationCreditMeter.RecordAsync` posts the atomic guarded debit (still
  post-LLM, best-effort; no idempotency key — see ADR-0033).
- **Invoicing** — `BuildAiCreditLineItemAsync` derives the post-paid remainder from ledger debits not
  covered by the subscription grant (single source in this change; multi-source allocation is change c).
- **Subscription grant** — `AiCreditsMonthly` is minted as a recurring `Subscription` grant idempotent on
  `(tenant, period_key)` (`expires_at = periodEnd`, no carryover) by a scheduled mint worker (mirrors
  `OverageInvoiceIssuanceWorker`).
- **Back-fill migration** — one-time current-period grant-minus-reconstructed-debits per tenant with a
  non-null `AiCreditsMonthly`, behind a feature flag gating the invoice-read flip until back-fill completes.

## Capabilities

### Modified Capabilities

- `ai-credit-ledger`: the substrate becomes the live source of truth for AI-credit enforcement, metering,
  and invoicing; `QuotaCheckResult` gains a structured `Outcome`. Delta authored at change start.

## Impact

- `Verbara.Platform.Billing` (`DefaultQuotaEnforcementService`, `QuotaCheckResult`, `BuildAiCreditLineItemAsync`),
  `Verbara.Platform.Api` (`BillingTypificationCreditMeter`, `ConversationEndpoints` outcome switch, mint worker
  + DI), `Storage.Postgres` (back-fill migration). Guarded by change (a)'s characterization tests.
  Authoritative design: ADR-0033.
