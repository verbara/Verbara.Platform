-- 028_VoiceAutoAnswer.sql
--
-- Phase 3B.2b (in-browser softphone auto-answer).
--
-- Two new columns drive the auto-answer cascade:
--   * agents.auto_answer — the per-agent override. NULLABLE on purpose: NULL means
--     "inherit the queue default", true/false is an explicit override. A plain
--     boolean-NOT-NULL would collapse the tri-state (no way to say "unset").
--   * queue_configs.auto_answer_default — the per-queue default an unset agent
--     inherits. NOT NULL DEFAULT false (the industry opt-in posture: manual answer
--     unless deliberately enabled). (The queue entity persists to queue_configs.)
--
-- The effective decision is computed client-side from /agents/me's auto_answer
-- and the call's queue default carried on the voice screen-pop event; the server
-- only stores the two inputs.
--
-- Idempotent (ADD COLUMN IF NOT EXISTS) so a re-attempted migration is a no-op.

ALTER TABLE agents ADD COLUMN IF NOT EXISTS auto_answer boolean;

ALTER TABLE queue_configs ADD COLUMN IF NOT EXISTS auto_answer_default boolean NOT NULL DEFAULT false;
