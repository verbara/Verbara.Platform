-- =============================================================================
-- Verbara.Platform — Reason Hints (002)
-- =============================================================================
-- Typification P1: maps a routing/entry context (the DID a call arrived on, the
-- channel, or the queue) to a captured reason taxonomy path, so the disposition
-- form can be prefilled with the most likely contact reason. Ties broken by
-- `priority`. Mirrors the `typification_bindings` table shape/conventions.
-- =============================================================================

CREATE TABLE IF NOT EXISTS reason_hints (
    tenant_id   TEXT    NOT NULL,
    hint_id     TEXT    NOT NULL,
    scope       TEXT    NOT NULL,
    scope_ref   TEXT    NOT NULL,
    reason_path TEXT    NOT NULL,
    priority    INT     NOT NULL DEFAULT 0,
    is_active   BOOLEAN NOT NULL DEFAULT TRUE,
    PRIMARY KEY (tenant_id, hint_id)
);
CREATE INDEX IF NOT EXISTS idx_reason_hints_scope ON reason_hints (tenant_id, scope, scope_ref);
