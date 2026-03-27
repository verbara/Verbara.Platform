-- =============================================================================
-- Asterisk.Platform — RBAC Granular (006)
-- =============================================================================

-- ---------------------------------------------------------------------------
-- Permission catalog (global, immutable, seeded)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS permissions (
    permission_id TEXT PRIMARY KEY,          -- "campaigns:campaign:delete"
    category TEXT NOT NULL,                  -- "campaigns"
    resource TEXT NOT NULL,                  -- "campaign"
    action TEXT NOT NULL,                    -- "delete"
    description TEXT NOT NULL,
    implies TEXT[]                           -- ["campaigns:campaign:edit", "campaigns:campaign:view"]
);

-- ---------------------------------------------------------------------------
-- Role templates (system defaults, read-only after seeding)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS role_templates (
    template_id TEXT PRIMARY KEY,            -- "supervisor"
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    is_system BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS role_template_permissions (
    template_id TEXT NOT NULL REFERENCES role_templates(template_id) ON DELETE CASCADE,
    permission_id TEXT NOT NULL REFERENCES permissions(permission_id) ON DELETE CASCADE,
    PRIMARY KEY (template_id, permission_id)
);

-- ---------------------------------------------------------------------------
-- Tenant roles (per-tenant, custom or cloned from template)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS tenant_roles (
    role_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    description TEXT,
    source_template_id TEXT,                 -- null = custom, "supervisor" = cloned
    is_default BOOLEAN NOT NULL DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, role_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_tenant_roles_name
    ON tenant_roles (tenant_id, lower(name));

CREATE TABLE IF NOT EXISTS tenant_role_permissions (
    tenant_id TEXT NOT NULL,
    role_id TEXT NOT NULL,
    permission_id TEXT NOT NULL REFERENCES permissions(permission_id) ON DELETE CASCADE,
    PRIMARY KEY (tenant_id, role_id, permission_id),
    FOREIGN KEY (tenant_id, role_id) REFERENCES tenant_roles(tenant_id, role_id) ON DELETE CASCADE
);

-- ---------------------------------------------------------------------------
-- User role assignments (multi-role)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS user_roles (
    tenant_id TEXT NOT NULL,
    user_id TEXT NOT NULL,
    role_id TEXT NOT NULL,
    assigned_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    assigned_by TEXT,
    PRIMARY KEY (tenant_id, user_id, role_id),
    FOREIGN KEY (tenant_id, role_id) REFERENCES tenant_roles(tenant_id, role_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_user_roles_user
    ON user_roles (tenant_id, user_id);

CREATE INDEX IF NOT EXISTS idx_user_roles_role
    ON user_roles (tenant_id, role_id);
