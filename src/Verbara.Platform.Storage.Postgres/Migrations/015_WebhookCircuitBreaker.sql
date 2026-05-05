-- 015_WebhookCircuitBreaker.sql
-- Adds circuit-breaker columns to webhook_subscriptions (v1.3.1 feature that
-- shipped the code in PostgresWebhookSubscriptionStore without a DB migration).

ALTER TABLE webhook_subscriptions
    ADD COLUMN IF NOT EXISTS circuit_status VARCHAR(20) NOT NULL DEFAULT 'closed',
    ADD COLUMN IF NOT EXISTS circuit_failures INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS circuit_opened_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS circuit_next_probe_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS circuit_probe_attempts INTEGER NOT NULL DEFAULT 0;
