-- 002_BillingSchema.sql — Metering Engine + Quota Enforcement tables

CREATE TABLE IF NOT EXISTS usage_records (
    record_id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    usage_type SMALLINT NOT NULL,
    quantity NUMERIC(18,6) NOT NULL,
    unit SMALLINT NOT NULL,
    channel TEXT,
    reference_id TEXT,
    recorded_at TIMESTAMPTZ NOT NULL,
    metadata JSONB
);

CREATE INDEX IF NOT EXISTS idx_usage_tenant_period ON usage_records (tenant_id, recorded_at DESC);
CREATE INDEX IF NOT EXISTS idx_usage_tenant_type ON usage_records (tenant_id, usage_type, recorded_at DESC);

CREATE TABLE IF NOT EXISTS tenant_quotas (
    tenant_id TEXT PRIMARY KEY,
    max_concurrent_channels INT NOT NULL DEFAULT 100,
    max_active_campaigns INT NOT NULL DEFAULT 10,
    max_monthly_voice_minutes BIGINT,
    max_monthly_messages BIGINT,
    max_storage_bytes BIGINT,
    max_active_agents INT,
    quota_action SMALLINT NOT NULL DEFAULT 0
);
