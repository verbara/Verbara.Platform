-- =============================================================================
-- Asterisk.Platform — Auth Enterprise (005)
-- =============================================================================

-- ALTER TABLE users — Auth fields
ALTER TABLE users ADD COLUMN IF NOT EXISTS password_hash TEXT;
ALTER TABLE users ADD COLUMN IF NOT EXISTS mfa_enabled BOOLEAN NOT NULL DEFAULT false;
ALTER TABLE users ADD COLUMN IF NOT EXISTS mfa_secret TEXT;
ALTER TABLE users ADD COLUMN IF NOT EXISTS mfa_recovery_codes TEXT[];
ALTER TABLE users ADD COLUMN IF NOT EXISTS mfa_confirmed_at TIMESTAMPTZ;
ALTER TABLE users ADD COLUMN IF NOT EXISTS email_verified BOOLEAN NOT NULL DEFAULT false;
ALTER TABLE users ADD COLUMN IF NOT EXISTS failed_login_attempts INT NOT NULL DEFAULT 0;
ALTER TABLE users ADD COLUMN IF NOT EXISTS locked_until TIMESTAMPTZ;
ALTER TABLE users ADD COLUMN IF NOT EXISTS password_changed_at TIMESTAMPTZ;
ALTER TABLE users ADD COLUMN IF NOT EXISTS last_login_at TIMESTAMPTZ;
ALTER TABLE users ADD COLUMN IF NOT EXISTS auth_provider TEXT NOT NULL DEFAULT 'local';
ALTER TABLE users ADD COLUMN IF NOT EXISTS external_id TEXT;

-- ALTER TABLE api_keys — Fix schema mismatch
ALTER TABLE api_keys ADD COLUMN IF NOT EXISTS user_id TEXT;

-- NEW TABLE refresh_tokens
CREATE TABLE IF NOT EXISTS refresh_tokens (
    token_id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    token_hash TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ,
    replaced_by TEXT,
    ip_address TEXT,
    user_agent TEXT
);
CREATE INDEX IF NOT EXISTS idx_refresh_tokens_hash ON refresh_tokens (token_hash) WHERE revoked_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_refresh_tokens_user ON refresh_tokens (tenant_id, user_id);

-- NEW TABLE auth_events (Append-Only)
CREATE TABLE IF NOT EXISTS auth_events (
    event_id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    user_id TEXT,
    event_type TEXT NOT NULL,
    ip_address TEXT,
    user_agent TEXT,
    details JSONB,
    created_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_auth_events_tenant ON auth_events (tenant_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_auth_events_user ON auth_events (tenant_id, user_id, created_at DESC);

-- NEW TABLE tenant_auth_config
CREATE TABLE IF NOT EXISTS tenant_auth_config (
    tenant_id TEXT PRIMARY KEY,
    mfa_policy TEXT NOT NULL DEFAULT 'optional',
    mfa_required_roles TEXT[] DEFAULT '{}',
    password_min_length INT NOT NULL DEFAULT 12,
    password_require_uppercase BOOLEAN NOT NULL DEFAULT true,
    password_require_number BOOLEAN NOT NULL DEFAULT true,
    password_require_special BOOLEAN NOT NULL DEFAULT false,
    lockout_threshold INT NOT NULL DEFAULT 5,
    lockout_duration_minutes INT NOT NULL DEFAULT 15,
    session_idle_timeout_minutes INT NOT NULL DEFAULT 30,
    session_absolute_timeout_hours INT NOT NULL DEFAULT 12,
    oidc_enabled BOOLEAN NOT NULL DEFAULT false,
    oidc_authority TEXT,
    oidc_client_id TEXT,
    oidc_client_secret TEXT,
    oidc_auto_create_users BOOLEAN NOT NULL DEFAULT true,
    oidc_default_role TEXT DEFAULT 'Agent',
    updated_at TIMESTAMPTZ
);
