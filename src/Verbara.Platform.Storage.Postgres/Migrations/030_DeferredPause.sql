-- 030_DeferredPause.sql — W4 deferred pause / pause-when-free.
-- Per-agent deferred aux-state target (NULL = no pending pause).
ALTER TABLE agents
  ADD COLUMN IF NOT EXISTS pending_state integer NULL,
  ADD COLUMN IF NOT EXISTS pending_reason text NULL,
  ADD COLUMN IF NOT EXISTS pending_since timestamptz NULL;
-- Per-tenant max minutes a pending pause may wait before the drain sweep
-- force-applies it + alerts the supervisor. Default 30. 0 disables the timeout.
ALTER TABLE tenant_auth_config
  ADD COLUMN IF NOT EXISTS pending_pause_timeout_minutes integer NOT NULL DEFAULT 30;
