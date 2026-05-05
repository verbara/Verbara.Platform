-- 006_OutboundWebhooks.sql
-- Outbound webhook subscription and delivery tables for Plan 30D

CREATE TABLE IF NOT EXISTS webhook_subscriptions (
    subscription_id VARCHAR(36) PRIMARY KEY,
    tenant_id VARCHAR(36) NOT NULL,
    name VARCHAR(200) NOT NULL,
    endpoint_url VARCHAR(2000) NOT NULL,
    secret VARCHAR(64) NOT NULL,
    event_types JSONB NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_webhook_subscriptions_tenant
    ON webhook_subscriptions(tenant_id);

CREATE TABLE IF NOT EXISTS webhook_deliveries (
    delivery_id VARCHAR(36) PRIMARY KEY,
    tenant_id VARCHAR(36) NOT NULL,
    subscription_id VARCHAR(36) NOT NULL REFERENCES webhook_subscriptions(subscription_id) ON DELETE CASCADE,
    event_type VARCHAR(100) NOT NULL,
    payload JSONB NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Pending',
    attempts INTEGER NOT NULL DEFAULT 0,
    max_attempts INTEGER NOT NULL DEFAULT 8,
    next_retry_at TIMESTAMPTZ,
    last_response_code INTEGER,
    last_error TEXT,
    created_at TIMESTAMPTZ NOT NULL,
    delivered_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_webhook_deliveries_pending
    ON webhook_deliveries(next_retry_at)
    WHERE status = 'Pending' AND next_retry_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_webhook_deliveries_subscription
    ON webhook_deliveries(subscription_id);

CREATE INDEX IF NOT EXISTS ix_webhook_deliveries_dead_letter
    ON webhook_deliveries(tenant_id)
    WHERE status = 'DeadLetter';
