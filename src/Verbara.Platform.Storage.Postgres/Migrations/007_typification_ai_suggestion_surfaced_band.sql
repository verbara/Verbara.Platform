-- =============================================================================
-- Verbara.Platform — AI Suggestion Surfaced Band (007)
-- =============================================================================
-- Additive migration (baseline squashed in 001_Baseline.sql). Adds the
-- `surfaced_band` column to `typification_ai_suggestions` so calibration can
-- EXCLUDE auto-filled samples: once a form is auto-filled the agent's
-- "acceptance" is biased (they rubber-stamp the AI's pick), so AutoFill-band
-- rows must not count toward the gate that decides whether AutoFill stays on.
-- Stored as the enum NAME (TEXT), consistent with how AiMode is persisted as a
-- resilient string. Legacy rows default to 'None'. Idempotent (ADD COLUMN IF
-- NOT EXISTS).
-- =============================================================================

ALTER TABLE typification_ai_suggestions
    ADD COLUMN IF NOT EXISTS surfaced_band TEXT NOT NULL DEFAULT 'None';
