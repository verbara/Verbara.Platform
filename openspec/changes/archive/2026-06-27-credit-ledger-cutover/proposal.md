---
tier: GRANDE
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

Change **(b)** of the AI-credit-ledger program (ADR-0033, incl. the **2026-06-27 addendum**). With the
substrate (change a) landed inert, this change performs the **cutover**: it re-points #4's three billing
seams onto the ledger and turns the monthly `AiCreditsMonthly` allowance into a recurring grant — so there
is one source of truth for "credits left". Behaviour stays **byte-identical for allowance-only tenants**
(postpaid overage preserved for `Warn`), and the highest-regression-risk step — the money-path invoice flip
— is gated by a production shadow-reconciliation window.

> Re-grounded against the post-(a) code (the seams, the substrate API, the characterization tests) and a
> judge-panel + completeness-critic design study that resolved the prepaid-vs-postpaid reconciliation
> (ADR-0033 addendum). PO decision (2026-06-27): **postpaid — preserve #4's overage** (Model C).

## What Changes

- **Metered debit is two-step (Model C), one transaction** — a new
  `ICreditLedgerStore.PostMeteredDebitAsync(tenant, debit, coveredSource, usageRecordId, ct)` on both store
  twins: `covered = min(balance, debit)` drawn from the prepaid stock via the guarded
  `UPDATE … WHERE balance >= @covered` (projection floors at 0 — prepaid lot un-overdrawable, `source =
  Subscription`); the **uncovered tail** `debit − covered` posted as an unconditional `source = PostPaid`
  ledger row that does **not** touch the projection. Returns covered/post-paid/new-balance.
- **Substrate source-label fix (mandatory)** — (a)'s `TryPostDebitAsync` hard-codes `source = PostPaid` on
  **every** debit; parameterise it so a covered draw records the lot it drew from. Left unfixed, a
  `Σ source=PostPaid` invoice over-bills 100% of consumption for every allowance-only tenant.
- **Quota** — `DefaultQuotaEnforcementService.CheckQuotaAsync` (AiAnalysis), behind the enforcement flag,
  reads the O(1) projection balance instead of recomputing from `usage_records`; add
  `QuotaOutcome { Allow, Warn, SoftBlock, HardBlock }` to `QuotaCheckResult` (4th positional, defaults
  `Allow`). Exhausted **prepaid** balance ⇒ `HardBlock`/`SoftBlock` per `QuotaAction`; a **`Warn` tenant
  overflows to `PostPaid` and is never hard-blocked at zero** (preserves #4). The classify endpoint switches
  on `Outcome` and drops the second `GetQuotaStatusAsync` read (this seam is flag-independent — both paths
  populate `Outcome`).
- **Metering** — `BillingTypificationCreditMeter.RecordAsync`, behind the enforcement flag, posts the
  two-step debit (post-LLM, best-effort, never breaks metering; no idempotency key — ADR-0033). `usage_records`
  are still written (audit/analytics); the threshold-notification straddle is unchanged.
- **Invoicing** — `BuildAiCreditLineItemAsync`, behind the **separate invoice-read flag**, derives
  customer-owed overage as `Σ (period debit rows where source = PostPaid)`. Until that flag flips it keeps
  reading `usage_records` (the shadow-reconciliation window).
- **Subscription grant + mint worker** — `AiCreditsMonthly` minted as a recurring `Subscription` grant
  idempotent on `(tenant, period_key)` (`expires_at = periodEnd`, no carryover) by a scheduled worker
  mirroring `OverageInvoiceIssuanceWorker` (keyed resilience policy, per-cycle scope, `internal` cycle method).
- **Current-period back-fill migration** — seeds each tenant with non-null `AiCreditsMonthly`: Subscription
  grant + reconstructed covered/PostPaid debits to the same consumed value, on the **frozen** ratio basis.
  Idempotent (seed debit `external_ref = "backfill:{period}"`). The enforcement flag must not flip until
  back-fill is 100% complete and one mint tick is confirmed.

## Capabilities

### Modified Capabilities

- `ai-credit-ledger`: the substrate becomes the live source of truth for AI-credit enforcement, metering,
  and (after the shadow window) invoicing; the debit is two-step (covered + PostPaid tail); `QuotaCheckResult`
  gains a structured `Outcome`. Delta in `specs/ai-credit-ledger/spec.md`.

## Impact

- `Verbara.Platform.Billing` — `ICreditLedgerStore` (new `PostMeteredDebitAsync` + parameterised debit
  source), `DefaultQuotaEnforcementService`, `QuotaCheckResult`/`QuotaOutcome`, `DefaultInvoiceGenerationService`,
  new `CreditGrantMintWorker`, the cutover feature-flag (added to the already-injected `PlatformLlmOptions`).
- `Verbara.Platform.Api` — `BillingTypificationCreditMeter` (two-step debit), `ConversationEndpoints` outcome
  switch, mint-worker + flag DI in `Program.cs`.
- `Storage.Postgres` / `Storage.InMemory` — `PostMeteredDebitAsync` twin impls; back-fill migration (013).
- Guarded by change (a)'s characterization tests (re-seeded against the ledger). Authoritative design:
  **ADR-0033 + its 2026-06-27 addendum**.
