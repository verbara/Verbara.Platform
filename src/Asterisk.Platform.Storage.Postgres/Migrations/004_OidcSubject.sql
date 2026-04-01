-- =============================================================================
-- Asterisk.Platform — OIDC Subject Migration (004)
-- =============================================================================
-- Adds oidc_subject column to users table for OIDC SSO user linking.
-- =============================================================================

ALTER TABLE users ADD COLUMN IF NOT EXISTS oidc_subject TEXT;

CREATE INDEX IF NOT EXISTS ix_users_oidc_subject
    ON users (tenant_id, oidc_subject) WHERE oidc_subject IS NOT NULL;
