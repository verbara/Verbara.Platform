-- 023_TenantIpAllowlist.sql
--
-- v1.3.0 IP Allowlist (per-tenant). Adds:
--   1) `tenant_ip_allowlist` table holding CIDR entries (Postgres native CIDR type
--      → IPv4 + IPv6 both supported, server-side validation rejects malformed
--      values at INSERT time).
--   2) `tenant_auth_config.ip_allowlist_enabled` flag — separate from the entries
--      so the operator can stage entries before flipping enforcement on. Default
--      FALSE keeps existing tenants unaffected.
--
-- See docs/specs/2026-05-02-ip-allowlist-design.md §2.

CREATE TABLE tenant_ip_allowlist (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id TEXT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    cidr CIDR NOT NULL,
    description TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    -- audit trail only; no FK to users(tenant_id, user_id) composite PK because
    -- this repo's convention is to store user_id TEXT without FK enforcement
    -- (see refresh_tokens, role_assignments, agents in 001_InitialSchema.sql).
    -- Keeping the value through user deletion is desired (compliance trail),
    -- so even if we wanted a composite FK we would still want ON DELETE
    -- SET NULL semantics.
    created_by_user_id TEXT,
    CONSTRAINT cidr_per_tenant_unique UNIQUE (tenant_id, cidr)
);

CREATE INDEX idx_tenant_ip_allowlist_tenant ON tenant_ip_allowlist(tenant_id);

ALTER TABLE tenant_auth_config
    ADD COLUMN ip_allowlist_enabled BOOLEAN NOT NULL DEFAULT FALSE;
