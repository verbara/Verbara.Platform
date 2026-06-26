-- =============================================================================
-- Verbara.Platform — Invoice due-date + payment-status persistence (011)
-- =============================================================================
-- Additive (baseline squashed in 001_Baseline.sql). Two columns for the
-- credit-overage dunning cycle (typification-credit-overage-dunning):
--   invoices.due_date       — when an issued overage invoice is payable. NULL
--     until the OverageInvoiceIssuanceWorker issues the draft (DunningService
--     skips invoices whose due_date is NULL, preserving today's behaviour).
--   invoices.payment_status — dunning lifecycle: 0=Current (default), 1=Overdue,
--     2=Delinquent, 3=WrittenOff. Stored as SMALLINT (enum ordinal), mirroring
--     the existing `status` column. Legacy rows default to 0 = Current.
-- Idempotent (ADD COLUMN IF NOT EXISTS — Postgres 18).
-- =============================================================================

ALTER TABLE invoices
    ADD COLUMN IF NOT EXISTS due_date TIMESTAMPTZ;

ALTER TABLE invoices
    ADD COLUMN IF NOT EXISTS payment_status SMALLINT NOT NULL DEFAULT 0;
