-- Migration 010: Tenant branding + notifications

CREATE TABLE IF NOT EXISTS tenant_branding (
    tenant_id          TEXT PRIMARY KEY REFERENCES tenants(tenant_id),
    display_name       TEXT,
    logo_url           TEXT,
    favicon_url        TEXT,
    primary_color      TEXT,
    secondary_color    TEXT,
    accent_color       TEXT,
    locale             TEXT,
    timezone           TEXT,
    subdomain          TEXT,
    support_email      TEXT,
    support_url        TEXT,
    email_from_name    TEXT,
    email_from_address TEXT,
    created_at         TIMESTAMPTZ NOT NULL,
    updated_at         TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_tenant_branding_subdomain
    ON tenant_branding (subdomain) WHERE subdomain IS NOT NULL;

CREATE TABLE IF NOT EXISTS notifications (
    notification_id  TEXT PRIMARY KEY,
    tenant_id        TEXT NOT NULL,
    user_id          TEXT,
    category         INTEGER NOT NULL,
    severity         INTEGER NOT NULL,
    type             TEXT NOT NULL,
    title            TEXT NOT NULL,
    body             TEXT NOT NULL,
    action_url       TEXT,
    is_read          BOOLEAN NOT NULL DEFAULT false,
    created_at       TIMESTAMPTZ NOT NULL,
    read_at          TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_notifications_user_unread
    ON notifications (tenant_id, user_id, created_at DESC)
    WHERE is_read = false;

CREATE INDEX IF NOT EXISTS ix_notifications_tenant_type_dedup
    ON notifications (tenant_id, type, created_at DESC);
