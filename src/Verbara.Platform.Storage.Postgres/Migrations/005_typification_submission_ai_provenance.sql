-- =============================================================================
-- Verbara.Platform — Typification Submission AI Provenance (005)
-- =============================================================================
-- Additive migration (baseline squashed in 001_Baseline.sql). Adds two nullable
-- columns to `typification_submissions` to capture the AI's suggestion at commit
-- time, enabling correction-signal analysis and calibration accuracy queries.
-- Idempotent (ADD COLUMN IF NOT EXISTS).
-- =============================================================================

ALTER TABLE typification_submissions
    ADD COLUMN IF NOT EXISTS suggested_leaf_node_id  TEXT  NULL,
    ADD COLUMN IF NOT EXISTS suggested_node_path     JSONB NULL;
