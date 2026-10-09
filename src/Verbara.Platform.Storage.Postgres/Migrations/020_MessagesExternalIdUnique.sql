-- =============================================================================
-- Verbara.Platform — one stored message per provider message id (020)
-- =============================================================================
-- whatsapp-works-for-real, slice 1, block B (design D10; message-delivery-correlation).
-- Inbound deduplication looked up external_message_id over the non-unique
-- idx_messages_external and then inserted, so two concurrent identical webhook
-- deliveries could both insert, and every later lookup by provider id
-- (QuerySingleOrDefault) threw on the second row. From here on at most one
-- message per (tenant_id, external_message_id) exists; inbound persistence uses
-- INSERT ... ON CONFLICT (tenant_id, external_message_id)
--   WHERE external_message_id IS NOT NULL DO NOTHING and re-reads.
--
-- Upgrade over existing duplicates: rows sharing (tenant_id,
-- external_message_id) are by definition the same provider message delivered
-- twice, so the earliest (created_at, then message_id) is kept and the others
-- are deleted. Nothing references a message row by id (no foreign keys, no
-- repointing needed). Rows without a provider id (outbound before a send, or
-- channels that have none) are untouched — the index is partial.
--
-- One DO block, so the steps are atomic even when a tool applies the file
-- outside a transaction (the runner wraps it in one anyway). The SHARE ROW
-- EXCLUSIVE lock keeps writers out between the dedupe and the index build, so
-- no duplicate can slip in after the DELETE; reads continue.
-- =============================================================================

DO $$
BEGIN
    LOCK TABLE messages IN SHARE ROW EXCLUSIVE MODE;

    DELETE FROM messages m
    USING messages keep
    WHERE m.external_message_id IS NOT NULL
      AND keep.tenant_id = m.tenant_id
      AND keep.external_message_id = m.external_message_id
      AND (keep.created_at, keep.message_id) < (m.created_at, m.message_id);

    CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_tenant_external
        ON messages (tenant_id, external_message_id)
        WHERE external_message_id IS NOT NULL;

    DROP INDEX IF EXISTS idx_messages_external;
END
$$;
