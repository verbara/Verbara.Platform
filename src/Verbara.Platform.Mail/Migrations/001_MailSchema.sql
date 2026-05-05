CREATE SCHEMA IF NOT EXISTS mail;

CREATE TABLE mail.oauth_tokens (
    id             TEXT PRIMARY KEY DEFAULT gen_random_uuid()::text,
    tenant_id      TEXT NOT NULL,
    user_id        TEXT NOT NULL,
    provider       TEXT NOT NULL DEFAULT 'microsoft',
    access_token   TEXT NOT NULL,
    refresh_token  TEXT NOT NULL,
    expires_at     TIMESTAMPTZ NOT NULL,
    scopes         TEXT NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, user_id, provider)
);

CREATE INDEX idx_oauth_tokens_expiring ON mail.oauth_tokens (expires_at) WHERE expires_at > now();
