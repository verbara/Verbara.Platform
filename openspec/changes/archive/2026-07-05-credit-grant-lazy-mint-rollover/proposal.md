---
tier: PEQUEÑO
owner: Harol
approver: Harol
stakeholder: Tenants with monthly AI-credit allowances
decision_ref: Platform/ADR-0033
---

# Proposal: credit-grant-lazy-mint-rollover

## Why

Known month-rollover window, named at the credit-ledger ship and until now recorded ONLY in a C#
doc-comment (`CreditGrantMintWorker` header, `src/Verbara.Platform.Billing/CreditGrantMintWorker.cs`)
citing an "ADR-0033 addendum": a tenant that first consumes after a UTC month boundary but before the
mint worker's next tick (≤ one `DunningConfig.CheckIntervalHours` interval) sees no current-period
`Subscription` grant yet — its balance read returns prior carry-over only, so quota decisions in that
window run against a stale (usually lower) balance. The named fast-follow is **lazy-mint-on-read**.
This change is its OpenSpec backlog home; the ADR-0033 addendum recording it lands alongside this
proposal.

## What Changes

Mint the current-period `Subscription` grant inline on the first balance read of a new period
(quota pre-check / readout path), idempotent via the existing `(tenant_id, period_key, entry_type)`
`ON CONFLICT DO NOTHING` posting — the worker remains the steady-state mint; the lazy mint only
closes the rollover window.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `ai-credit-ledger`: adds the lazy-mint-on-read requirement alongside "Monthly allowance is a
  recurring subscription grant".

## Impact

`Verbara.Platform.Billing` (balance-read path / ledger store call-site), no API surface change.
The mint stays idempotent, so worker + lazy mint can race safely.

## Architectural Risk

**Level:** LOW-MEDIUM — touches the balance read on the quota path (hot path); the mint must not
add a write to every read (only when the current-period grant is absent). **Affected:** Billing
quota/readout paths. **Mitigation:** grant-existence check is an indexed lookup; the mint itself is
the existing idempotent posting; concurrency covered by `ON CONFLICT DO NOTHING` + projection
conditional upsert (proven in the c-train); deterministic tests per test-determinism fences.
