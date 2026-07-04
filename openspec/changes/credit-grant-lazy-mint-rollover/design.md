# Design — credit-grant-lazy-mint-rollover

Backlog change: finalized at apply time.

- **Placement:** the ledger-path balance read used by quota enforcement (behind the enforcement
  flag) and the credits readout. The check is "does a `Subscription` grant with the current
  `period_key` exist?" — an indexed lookup on the existing partial unique index; only on miss does
  the mint fire (reuse the exact posting path `PostGrantAsync` the worker uses).
- **No new idempotency machinery:** the existing `(tenant_id, period_key, entry_type)`
  `ON CONFLICT DO NOTHING` + conditional projection upsert already makes worker/lazy races safe
  (proven in the c-train tests).
- **Do NOT** move the back-fill or expiry logic here; scope is strictly the rollover window.
- **Tests:** deterministic (FakeTimeProvider for the period boundary per test-determinism);
  concurrency test mirrors the existing "duplicate subscription grant is a no-op" scenario;
  live-DB Postgres coverage for the ON CONFLICT path.
- **References:** Platform/ADR-0033 (+ 2026-07-04 lazy-mint addendum), CreditGrantMintWorker
  doc-comment (the original in-code record of this window).
