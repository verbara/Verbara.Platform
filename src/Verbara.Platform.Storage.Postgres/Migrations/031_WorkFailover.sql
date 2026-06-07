-- 031_WorkFailover.sql — W5 (digital) in-flight work failover.
-- When the agent entered Offline (UTC); the failover sweep measures grace from here.
ALTER TABLE agents
  ADD COLUMN IF NOT EXISTS offline_since timestamptz NULL;
-- Per-tenant grace (seconds) the owner must be Offline before their orphaned
-- conversations are re-queued. Default 30. 0 disables failover for the tenant.
ALTER TABLE tenant_auth_config
  ADD COLUMN IF NOT EXISTS work_failover_grace_seconds integer NOT NULL DEFAULT 30;
-- Queue ordering priority (lower = earlier). Failover re-queues at -1 (front);
-- normal queued conversations stay 0 and order by created_at within priority.
ALTER TABLE conversations
  ADD COLUMN IF NOT EXISTS queue_priority integer NOT NULL DEFAULT 0;
