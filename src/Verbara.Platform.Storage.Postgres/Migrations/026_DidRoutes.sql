-- 026_DidRoutes.sql
--
-- Phase 2 (voice inbound routing) — `did_routes` table.
--
-- Maps an inbound DID (Direct Inward Dialing number) to a target queue per
-- tenant. The Stasis voice consumer (Phase 2.x) resolves the destination queue
-- for a PSTN/SIP inbound leg by looking the dialed DID up here. This migration
-- ships the data layer only (Phase 2.1); the consumer is wired separately.
--
-- Semantics:
--   * (tenant_id, route_id) is the primary key — route_id is a TEXT EntityId
--     (Guid "N" format), matching queues/agents convention (NOT a bigint
--     sequence like trunks/outbound_routes).
--   * A DID is unique per tenant (`did_per_tenant_unique`). The store layer
--     surfaces a violation of this constraint as an InvalidOperationException
--     so the API can return HTTP 409 Conflict instead of a 500.
--   * is_active gates whether the route participates in resolution. Inactive
--     routes are retained for audit/history but skipped by ListActiveAsync /
--     the active-DID lookup index.
--   * tenant_id FK → tenants(tenant_id) ON DELETE CASCADE so deleting a tenant
--     reaps its routes (mirrors queue_memberships, tenant_ip_allowlist).
--
-- Index rationale: the hot path is GetByDidAsync — resolving the active route
-- for a dialed number. A partial index on (tenant_id, did) WHERE is_active
-- keeps the active working set small and matches that predicate exactly. The
-- did_per_tenant_unique constraint already covers the full (tenant_id, did)
-- write path, so this partial index is read-optimization only.

CREATE TABLE IF NOT EXISTS did_routes (
    route_id   TEXT        NOT NULL,
    tenant_id  TEXT        NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    did        TEXT        NOT NULL,
    queue_id   TEXT        NOT NULL,
    is_active  BOOLEAN     NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, route_id),
    CONSTRAINT did_per_tenant_unique UNIQUE (tenant_id, did)
);

CREATE INDEX IF NOT EXISTS idx_did_routes_did
    ON did_routes (tenant_id, did)
    WHERE is_active;
