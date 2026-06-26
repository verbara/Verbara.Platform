-- =============================================================================
-- Verbara.Platform — AI-credit ledger substrate (012)
-- =============================================================================
-- Additive (baseline squashed in 001_Baseline.sql). The append-only signed
-- AI-credit ledger + its O(1) balance projection (ADR-0033 / the
-- credit-ledger-substrate spec delta):
--   ai_credit_ledger       — immutable signed entries (grants +, debits −). Never
--     updated or deleted; corrections are offsetting entries. SUM(amount) over a
--     tenant's live entries == tenant_credit_balance.balance (offline audit).
--   tenant_credit_balance  — O(1) maintained projection. The request-path balance
--     read is a primary-key lookup of this row, NEVER a SUM over the ledger.
-- Convention (ADR-0022): TEXT ids (EntityId hex), SMALLINT enum ordinals,
-- NUMERIC(18,6) amounts, TIMESTAMPTZ timestamps.
-- Idempotency indexes:
--   uq_ai_credit_ledger_period   — subscription-grant idempotency on
--     (tenant_id, period_key, entry_type), partial WHERE period_key IS NOT NULL.
--   uq_ai_credit_ledger_extref   — top-up idempotency on (tenant_id, external_ref),
--     partial WHERE external_ref IS NOT NULL.
-- This substrate is INERT — nothing reads or writes the ledger at runtime yet.
-- Idempotent (CREATE … IF NOT EXISTS — Postgres 18).
-- =============================================================================

CREATE TABLE IF NOT EXISTS ai_credit_ledger (
    entry_id        TEXT PRIMARY KEY,
    tenant_id       TEXT NOT NULL,
    entry_type      SMALLINT NOT NULL,
    source          SMALLINT NOT NULL,
    amount          NUMERIC(18,6) NOT NULL,
    period_key      TEXT,
    external_ref    TEXT,
    expires_at      TIMESTAMPTZ,
    usage_record_id TEXT,
    created_at      TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_ai_credit_ledger_tenant_created
    ON ai_credit_ledger (tenant_id, created_at);

CREATE UNIQUE INDEX IF NOT EXISTS uq_ai_credit_ledger_period
    ON ai_credit_ledger (tenant_id, period_key, entry_type)
    WHERE period_key IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_ai_credit_ledger_extref
    ON ai_credit_ledger (tenant_id, external_ref)
    WHERE external_ref IS NOT NULL;

CREATE TABLE IF NOT EXISTS tenant_credit_balance (
    tenant_id  TEXT PRIMARY KEY,
    balance    NUMERIC(18,6) NOT NULL,
    version    BIGINT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);
