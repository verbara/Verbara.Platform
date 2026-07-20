-- =============================================================================
-- Verbara.Platform — Dialer license-enforcement audit sink (017)
-- =============================================================================
-- dialer-license-audit-sink (decision_ref Pro/ADR-0016). Additive, back-compatible
-- storage for the tick-scoped license-enforcement episode delivered by the Pro
-- DialerEngine through IDialerLicenseAuditSink.RecordAsync — persisted by
-- PostgresDialerLicenseAuditSink. Without a registered sink the engine resolves
-- GetService<IDialerLicenseAuditSink>() -> null and silently drops the record
-- (the live compliance blind-spot this change closes).
--
-- Columns map 1:1 onto DialerLicenseAuditRecord (SchemaVersion 1) as frozen by
-- fixtures/dialer-license-audit-record.v1.json — the verbatim-fixture-citation rule.
-- The nullable columns are EXACTLY the record's nullable fields: reason,
-- reason_sequence, license_id, licensee. The event / reason / tier enums persist as
-- text; the Campaigns snapshot (IReadOnlyList<QuiescedCampaignInfo>) persists as jsonb.
--
-- Additive only: CREATE TABLE IF NOT EXISTS, no change to any existing table, no
-- backfill (design D3). The (occurred_at DESC) index supports a future report read
-- without committing to that surface here.
-- =============================================================================

CREATE TABLE IF NOT EXISTS dialer_license_audit (
    id                        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    schema_version            INTEGER     NOT NULL,   -- SchemaVersion
    event                     TEXT        NOT NULL,   -- Event (enum, as text)
    occurred_at               TIMESTAMPTZ NOT NULL,   -- OccurredAt
    tick_sequence             BIGINT      NOT NULL,   -- TickSequence
    engine_instance_id        UUID        NOT NULL,   -- EngineInstanceId
    reason                    TEXT        NULL,       -- Reason (enum, as text; null for Recovered)
    reason_sequence           TEXT        NULL,       -- ReasonSequence
    consecutive_blocked_ticks INTEGER     NOT NULL,   -- ConsecutiveBlockedTicks
    campaigns                 JSONB       NOT NULL,   -- Campaigns ('[]' for non-quiesce events)
    in_flight_at_quiesce      INTEGER     NOT NULL,   -- InFlightAtQuiesce
    license_id                TEXT        NULL,       -- LicenseId
    licensee                  TEXT        NULL,       -- Licensee
    tier                      TEXT        NOT NULL,   -- Tier (enum, as text)
    campaigns_rebuilt         INTEGER     NOT NULL    -- CampaignsRebuilt
);

-- Newest-first read for the future compliance report (design D3, Open Questions).
CREATE INDEX IF NOT EXISTS idx_dialer_license_audit_occurred
    ON dialer_license_audit (occurred_at DESC);

-- Correlate every episode from one engine process lifetime.
CREATE INDEX IF NOT EXISTS idx_dialer_license_audit_engine
    ON dialer_license_audit (engine_instance_id, occurred_at DESC);
