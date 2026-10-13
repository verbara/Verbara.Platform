-- =============================================================================
-- Verbara.Platform — supervisor silent-listen profiles (022)
-- =============================================================================
-- supervisor-silent-listen (design D3, D8, D12; Platform/ADR-0042).
-- One row per (tenant, user): the SIP credentials of the user's receive-only
-- listen endpoint (option B) and the listen phone an administrator stored for
-- them (option C, strict E.164). This is NOT an agents row: it never makes the
-- user a licensed agent.
--
-- sip_password gets the same care as agents.sip_password: it crosses HTTP only to
-- its owner, through POST /api/v1/supervisor/listen-endpoint.
--
-- Additive: a new table and its index. Rolled back by dropping the table; nothing
-- else reads it. Idempotent (IF NOT EXISTS).
-- =============================================================================

CREATE TABLE IF NOT EXISTS supervisor_listen_profiles (
    tenant_id     TEXT NOT NULL,
    user_id       TEXT NOT NULL,
    sip_extension VARCHAR(20),
    sip_password  VARCHAR(80),
    phone_number  VARCHAR(16),
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, user_id)
);

-- The listen extension is unique per tenant (it is the SIP auth username's suffix).
CREATE UNIQUE INDEX IF NOT EXISTS ux_supervisor_listen_profiles_extension
    ON supervisor_listen_profiles (tenant_id, sip_extension)
    WHERE sip_extension IS NOT NULL;
