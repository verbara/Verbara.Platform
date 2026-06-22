-- =============================================================================
-- Verbara.Platform — Per-tenant LLM config (009)
-- =============================================================================
-- Additive migration (baseline squashed in 001_Baseline.sql). Creates the
-- `tenant_llm_config` table used by Typification P2c.1 to store each tenant's
-- bring-your-own (BYO) LLM provider + encrypted credentials. One row per tenant
-- (MVP). The API key is `IDataProtector`-protected at rest (purpose
-- `Verbara.Platform.Typification.TenantLlmApiKey.v1`); `api_key_last4` is a
-- non-secret display hint. Type-specific config lives in the `provider_settings`
-- JSONB column so a new provider type needs NO DB migration.
-- `tenant_id` is TEXT (matches EntityId usage across the platform), NOT uuid.
-- Idempotent (CREATE TABLE IF NOT EXISTS).
-- =============================================================================

CREATE TABLE IF NOT EXISTS tenant_llm_config (
    tenant_id          TEXT        NOT NULL,
    provider_type      TEXT        NOT NULL,
    model              TEXT        NOT NULL,
    api_key_encrypted  TEXT,
    api_key_last4      TEXT,
    provider_settings  JSONB       NOT NULL DEFAULT '{}',
    enabled            BOOLEAN     NOT NULL DEFAULT false,
    created_at         TIMESTAMPTZ NOT NULL,
    updated_at         TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id)
);
