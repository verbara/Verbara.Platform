---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: Platform maintainer (spec readers, future change authors)
decision_ref: Platform/ADR-0033
---

# Proposal: reconcile-ai-credit-specs

## Why

The 2026-07-04 OpenSpec ecosystem audit (adversarially verified) found that the four AI-credit
living specs are **change-shaped, not capability-shaped**, and that `ai-credit-ledger` carries
three internal contradictions left behind by the c(a)→c2 change train:

1. **Stale transitional requirement** — "Substrate is inert (no behaviour change)" still asserts
   "Nothing reads or writes the ledger at runtime yet" and "`ai_credit_ledger` SHALL remain empty",
   contradicted by a later requirement in the same spec (flag-gated derivation) and by shipped code
   (`CreditGrantMintWorker` registered in `Program.cs` writes grants unconditionally).
2. **Superseded API contract** — "Metered consumption is a two-step covered-plus-PostPaid debit"
   specifies `PostMeteredDebitAsync(tenantId, debit, coveredSource, usageRecordId, ct)`; the
   `coveredSource` parameter no longer exists (superseded by the c2 FIFO-lots allocation, which is
   also specified — correctly — later in the same spec).
3. **c1/c2 scope contradiction** — the c1 permissions requirement promises "the `partner_admin`
   grant lands with the partner-scoped endpoint in c2", while the c2 requirement mandates "No new
   RBAC permissions or role-template seeding SHALL be added in c2" (the shipped resolution).

Additionally, **quota enforcement is specified in three specs and invoice overage in two** with the
legacy (`usage_records`) accounts stated unconditionally — contradicting the ledger's flag-gated
account when specs are read individually. Five specs still carry the archive-generated
`Purpose: TBD` placeholder, and `typification-platform-llm` carries a dangling `roadmap_ref`
and a change-shaped Architectural Risk tail describing only the original P2c.2 change.

Anyone reading a single spec in isolation gets a wrong or contradictory picture of the money path.

## What Changes

Docs-only — no code, no behaviour change. All edits are to `openspec/specs/**`:

- **`ai-credit-ledger`**: retire the inert-substrate requirement (rewritten as the
  characterization-baseline requirement it actually established); rewrite the two-step debit
  requirement to the current lot-driven contract (real signature, defer allocation detail to the
  FIFO-lots requirement); fix the c1 permissions requirement to match the c2 resolution and record
  the partner-scoped self-service top-up as an explicit deferred follow-up (not a forward promise).
  Declare the ledger the **single owner** of the quota decision and invoice-overage computation.
- **`typification-platform-llm`**: condition the "Monthly AI-credit allowance is enforced before
  classify" requirement on the credit-ledger enforcement flag being OFF (legacy path), deferring the
  flag-ON account to `ai-credit-ledger`; replace the dangling `roadmap_ref` frontmatter with
  `decision_ref`; replace the change-shaped Architectural Risk tail with a capability-scoped note.
- **`ai-credit-metering`**: scope its quota-enforcement requirement as the *pricing-basis* input to
  the legacy path, cross-referencing `ai-credit-ledger` as the outcome owner.
- **`ai-credit-billing`**: condition the allowance-based overage line item on the invoice-read flag
  being OFF, cross-referencing the ledger's `Σ |PostPaid debits|` account for the flag-ON path.
- **Purposes**: fill the `TBD` Purpose in `ai-credit-ledger`, `ai-credit-metering`,
  `ai-credit-billing`, `ai-credits-readout` (and `test-determinism`, `typification-autonomous-disposition`
  if still TBD) with one-paragraph capability statements.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `ai-credit-ledger`: retire/rewrite 3 contradictory requirements; declare single ownership of
  quota-decision + invoice-overage computation.
- `typification-platform-llm`: flag-condition the quota pre-check requirement (legacy path only).
- `ai-credit-metering`: scope differentiated-credit aggregation as pricing basis, not outcome owner.
- `ai-credit-billing`: flag-condition the allowance-based overage computation (legacy path only).

## Impact

- `openspec/specs/{ai-credit-ledger,typification-platform-llm,ai-credit-metering,ai-credit-billing,ai-credits-readout,test-determinism,typification-autonomous-disposition}/spec.md` only.
- No code, endpoints, DTOs, storage, or CI are touched. `openspec validate --specs --strict` must
  remain green. Spec-vs-code accuracy of every rewritten requirement is verified against current
  `main` source as part of this change.

## Architectural Risk

**Level:** LOW — documentation-only; no runtime artifact changes. **Affected:** future change
authors and AI artifact generation that read these specs (wrong-context risk removed, none added).
**Mitigation:** every rewritten requirement is checked against the shipped code (signatures,
Program.cs registrations, RBAC seeder) before merge; strict CLI validation gates the PR.
