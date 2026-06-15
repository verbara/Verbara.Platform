-- =============================================================================
-- Verbara.Platform — AI Suggestion Shadow Store (004)
-- =============================================================================
-- Additive migration (baseline squashed in 001_Baseline.sql). Creates the
-- `typification_ai_suggestions` table used by the P2b shadow/provenance store
-- to record every AI suggestion emitted by the typification classifier verbatim,
-- enabling server-side shadow mode, provenance derivation, and calibration
-- accuracy queries. Idempotent (CREATE TABLE IF NOT EXISTS + IF NOT EXISTS indexes).
-- =============================================================================

CREATE TABLE IF NOT EXISTS typification_ai_suggestions (
    id                      TEXT             NOT NULL,
    tenant_id               TEXT             NOT NULL,
    conversation_id         TEXT             NOT NULL,
    schema_id               TEXT             NOT NULL,
    schema_version          INT              NOT NULL,
    suggested_leaf_node_id  TEXT             NOT NULL,
    suggested_node_path     JSONB            NOT NULL DEFAULT '[]',
    suggested_field_values  JSONB            NOT NULL DEFAULT '{}',
    confidence              DOUBLE PRECISION NOT NULL,
    sentiment               TEXT,
    model_id                TEXT             NOT NULL,
    prompt_version          TEXT             NOT NULL,
    created_at              TIMESTAMPTZ      NOT NULL,
    -- Reconciliation fields: NULL until MarkReconciledAsync is called.
    committed_leaf_node_id  TEXT,
    accepted                BOOLEAN,
    PRIMARY KEY (id)
);

-- Supports GetLatestForConversationAsync: most-recent-by-tenant+conversation.
CREATE INDEX IF NOT EXISTS idx_ai_suggestions_conversation
    ON typification_ai_suggestions (tenant_id, conversation_id, created_at DESC);

-- Supports QueryAccuracyAsync: accuracy aggregation filtered by schema + date.
CREATE INDEX IF NOT EXISTS idx_ai_suggestions_schema_accuracy
    ON typification_ai_suggestions (tenant_id, schema_id, created_at DESC);
