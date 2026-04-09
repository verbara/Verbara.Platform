-- Migration 013: Dunning records, tenant add-ons, agent capacity persistence

CREATE TABLE IF NOT EXISTS dunning_records (
    dunning_id      VARCHAR(64) PRIMARY KEY,
    tenant_id       VARCHAR(64) NOT NULL,
    invoice_id      VARCHAR(64) NOT NULL,
    current_stage   VARCHAR(32) NOT NULL,
    started_at      TIMESTAMPTZ NOT NULL,
    escalated_at    TIMESTAMPTZ,
    resolved_at     TIMESTAMPTZ,
    is_paused       BOOLEAN NOT NULL DEFAULT FALSE,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_dunning_records_tenant ON dunning_records(tenant_id) WHERE is_active = TRUE;
CREATE INDEX idx_dunning_records_invoice ON dunning_records(invoice_id) WHERE is_active = TRUE;

CREATE TABLE IF NOT EXISTS tenant_add_ons (
    tenant_id       VARCHAR(64) NOT NULL,
    feature         VARCHAR(64) NOT NULL,
    enabled_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, feature)
);

CREATE INDEX idx_tenant_add_ons_tenant ON tenant_add_ons(tenant_id);

CREATE TABLE IF NOT EXISTS agent_capacity (
    tenant_id   VARCHAR(64) NOT NULL,
    agent_id    VARCHAR(64) NOT NULL,
    voice_load  INTEGER NOT NULL DEFAULT 0,
    chat_load   INTEGER NOT NULL DEFAULT 0,
    email_load  INTEGER NOT NULL DEFAULT 0,
    sms_load    INTEGER NOT NULL DEFAULT 0,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, agent_id)
);
