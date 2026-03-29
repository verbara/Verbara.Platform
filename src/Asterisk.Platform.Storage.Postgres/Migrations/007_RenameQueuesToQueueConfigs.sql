-- =============================================================================
-- Asterisk.Platform — Rename queues → queue_configs (007)
-- =============================================================================
-- Platform's queue business-logic table (SLA, overflow, skills) was named
-- "queues" which collides with Asterisk Realtime's "queues" table (app_queue).
-- Rename to "queue_configs" to free the name for Asterisk.
-- PostgreSQL automatically updates all FK constraints referencing this table.
-- =============================================================================

ALTER TABLE IF EXISTS queues RENAME TO queue_configs;
