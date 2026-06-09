-- =============================================================================
-- Verbara.Platform — Typification created_at (003)
-- =============================================================================
-- Adds a `created_at` column to `typification_bindings` and `reason_hints` so
-- equal-priority same-scope resolution is DETERMINISTIC (most-recent config wins)
-- instead of tie-breaking on a random EntityId. Idempotent; existing rows default
-- to now() so prior data keeps a stable, monotonic-enough ordering anchor.
-- =============================================================================

ALTER TABLE typification_bindings ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT now();
ALTER TABLE reason_hints         ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT now();
