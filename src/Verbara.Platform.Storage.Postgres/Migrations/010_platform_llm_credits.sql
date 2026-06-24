-- =============================================================================
-- Verbara.Platform — Platform-managed LLM + AI credit allowance (010)
-- =============================================================================
-- Additive (baseline squashed in 001_Baseline.sql). Two columns for P2c.2:
--   tenant_llm_config.ai_source  — 'Byo' (default) vs 'PlatformManaged'. Stored
--     as TEXT (enum .ToString() name, mirroring provider_type) — no smallint.
--   tenant_quotas.ai_credits_monthly — monthly AI-Credit allowance (1 credit =
--     PlatformLlmOptions.CreditTokenRatio tokens). NULL = unlimited / pay-go.
-- Idempotent (ADD COLUMN IF NOT EXISTS — Postgres 18).
-- =============================================================================

ALTER TABLE tenant_llm_config
    ADD COLUMN IF NOT EXISTS ai_source TEXT NOT NULL DEFAULT 'Byo';

ALTER TABLE tenant_quotas
    ADD COLUMN IF NOT EXISTS ai_credits_monthly BIGINT;
