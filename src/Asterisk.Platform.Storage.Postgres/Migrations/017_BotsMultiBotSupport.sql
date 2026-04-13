-- Migration 017: Multi-bot CRUD support
--
-- Adds `created_at` to `bot_configurations` for list ordering and makes
-- `default_flow_id` nullable so bots can be created without a flow bound yet
-- (the UI allows "no flow" selection; orchestrator guards null at runtime).

ALTER TABLE bot_configurations
    ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT now();

ALTER TABLE bot_configurations
    ALTER COLUMN default_flow_id DROP NOT NULL;
