-- 029_AgentLivenessTimeout.sql — W3 server-side agent liveness.
-- Per-tenant Redis presence-key TTL (seconds). Default 60 matches
-- TenantAuthConfig.AgentLivenessTimeoutSeconds. 0 disables liveness reaping.
ALTER TABLE tenant_auth_config
  ADD COLUMN IF NOT EXISTS agent_liveness_timeout_seconds integer NOT NULL DEFAULT 60;
