-- =============================================================================
-- Verbara.Platform — Append-only operator corrections of autonomous dispositions (015)
-- =============================================================================
-- ADR-0034 Decision 5: a supervisor correction of an autonomously stamped
-- disposition creates a SEPARATE, append-only correction record — the original
-- AutoAi TypificationSubmission (leaf, node path, confidence, Source) stays
-- IMMUTABLE (only its correction_state is flipped to a status pointer, migration
-- 014). The conversation is NOT reopened.
--
-- One correction per conversation (PRIMARY KEY (tenant_id, conversation_id)):
-- a conversation is correctable at most once — the endpoint's AlreadyCorrected
-- guard enforces this in the domain and this PK enforces it in the store.
--
-- corrected_node_path is the human-chosen root→leaf path as a JSONB string array
-- (mirrors typification_submissions.selected_node_path). Idempotent
-- (CREATE TABLE IF NOT EXISTS); tenant_id is TEXT (EntityId), NOT uuid. Ordered
-- after 014 (StringComparer.Ordinal migration discovery).
-- =============================================================================

CREATE TABLE IF NOT EXISTS typification_submission_corrections (
    tenant_id             TEXT        NOT NULL,
    conversation_id       TEXT        NOT NULL,
    corrected_leaf_node_id TEXT       NOT NULL,
    corrected_node_path   JSONB       NOT NULL,
    corrected_by_user_id  TEXT        NOT NULL,
    corrected_at          TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id, conversation_id)
);
