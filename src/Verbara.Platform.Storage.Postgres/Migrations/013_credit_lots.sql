-- =============================================================================
-- Verbara.Platform — AI-credit lot-allocation substrate (013)
-- =============================================================================
-- Additive over the 012 ledger substrate. The mutable per-grant lot projection +
-- the debit→lot allocation linkage that turn the single fungible balance into a
-- FIFO-consumed multi-source stock (ADR-0033 / the 2026-06-28 (c2) resolution
-- addendum — change credit-ledger-lots):
--   credit_lot       — one mutable row per grant. remaining is guarded-decremented
--     (CHECK remaining >= 0), expires_at is finally consulted, lot_seq is the
--     deterministic monotonic per-tenant FIFO tiebreak (NOT the random EntityId).
--     The load-bearing invariant: Σ(open non-expired lot.remaining) ==
--     tenant_credit_balance.balance.
--   credit_allocation — one INTERNAL row per (debit × drawn lot): the debit→lot
--     linkage recording each FIFO draw. Invisible to GetEntries(Count)Async (which
--     stay scoped to ai_credit_ledger), so c1's balance/entries API is unchanged.
--   credit_lot_seq   — the per-tenant monotonic lot_seq source (next_seq), bumped
--     under the projection lock by the store (Group 2); created here only.
-- Total/lock order (the deadlock-avoidance contract, equal to the consumption
-- order, obeyed by every writer): tenant_credit_balance row FIRST, then lots by
-- billable_priority ASC, expires_at ASC NULLS LAST, granted_at ASC, lot_seq ASC.
-- billable_priority is a STATIC draw-priority map (Promo=0, Partner=1,
-- Subscription=2, TopUp=2), DISTINCT from the persistence source ordinal — never
-- ORDER BY source. The FIFO/lock index here orders by source for index locality;
-- the store applies the priority map in its CASE / OrderBy.
-- Convention (ADR-0022): TEXT ids (EntityId hex), SMALLINT enum ordinals,
-- NUMERIC(18,6) amounts, TIMESTAMPTZ timestamps.
-- This substrate is INERT — nothing reads or writes lots at runtime yet (Group 1).
-- Idempotent + safely re-runnable (CREATE … IF NOT EXISTS — Postgres 18; the
-- back-fill blocks use ON CONFLICT DO NOTHING).
-- =============================================================================

CREATE TABLE IF NOT EXISTS credit_lot (
    lot_id      TEXT PRIMARY KEY,
    tenant_id   TEXT NOT NULL,
    source      SMALLINT NOT NULL,
    original    NUMERIC(18,6) NOT NULL,
    remaining   NUMERIC(18,6) NOT NULL CHECK (remaining >= 0),
    expires_at  TIMESTAMPTZ,
    granted_at  TIMESTAMPTZ NOT NULL,
    lot_seq     BIGINT NOT NULL
);

CREATE TABLE IF NOT EXISTS credit_allocation (
    allocation_id  TEXT PRIMARY KEY,
    debit_entry_id TEXT NOT NULL,
    lot_id         TEXT NOT NULL,
    source         SMALLINT NOT NULL,
    amount         NUMERIC(18,6) NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS credit_lot_seq (
    tenant_id TEXT PRIMARY KEY,
    next_seq  BIGINT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_credit_lot_fifo
    ON credit_lot (tenant_id, source, expires_at, granted_at, lot_seq);

CREATE INDEX IF NOT EXISTS idx_credit_allocation_debit
    ON credit_allocation (debit_entry_id);

CREATE INDEX IF NOT EXISTS idx_credit_allocation_lot
    ON credit_allocation (lot_id);

-- Existing-balance → lots back-fill. Existing tenants have grants + a balance
-- projection but no lots; the append-only ledger cannot reconstruct per-grant
-- remaining (pre-c2 debits aren't lot-linked). Seed ONE synthetic
-- Subscription-priority (source = 0) migration lot per tenant with
-- remaining = current projection.balance, expires_at = NULL, lot_seq = 0 — so the
-- invariant Σ(open non-expired lot.remaining) == balance holds from day one
-- without inventing per-grant history. Re-runnable: ON CONFLICT (lot_id) DO NOTHING.
INSERT INTO credit_lot (lot_id, tenant_id, source, original, remaining, expires_at, granted_at, lot_seq)
SELECT 'miglot-' || tenant_id, tenant_id, 0, balance, balance, NULL, updated_at, 0
FROM tenant_credit_balance
WHERE balance > 0
ON CONFLICT (lot_id) DO NOTHING;

-- Seed the per-tenant lot sequence past the migration lot (lot_seq = 0) so the
-- next minted lot starts at 1. Re-runnable: ON CONFLICT (tenant_id) DO NOTHING.
INSERT INTO credit_lot_seq (tenant_id, next_seq)
SELECT tenant_id, 1
FROM tenant_credit_balance
WHERE balance > 0
ON CONFLICT (tenant_id) DO NOTHING;
