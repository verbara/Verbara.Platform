-- =============================================================================
-- Verbara.Platform — Schema Binding AI Config Override (008)
-- =============================================================================
-- Additive migration (baseline squashed in 001_Baseline.sql). Adds one nullable
-- JSONB column to `typification_bindings` so a single binding (e.g. one queue or
-- campaign) can OVERRIDE the schema's AiConfig (E1) — piloting a different AI
-- automation band on one scope without changing the schema default. NULL = inherit
-- the schema's AiConfig; the effective config resolved at runtime is
-- `ai_config_override ?? schema.AiConfig`.
-- Idempotent (ADD COLUMN IF NOT EXISTS).
-- =============================================================================

ALTER TABLE typification_bindings
    ADD COLUMN IF NOT EXISTS ai_config_override JSONB NULL;
