-- =============================================================================
-- Verbara.Platform — outbound webhook delivery claims (023)
-- =============================================================================
-- webhook-delivery-runs-once (design D1; Platform/ADR-0043).
-- A delivery is claimed on its row before each POST, so that at most one attempt
-- is in flight, whichever path sends it (the immediate channel or the retry poll)
-- and on however many instances share this database. The claim is two columns:
--   * claim_id         — the fencing id of the attempt holding the claim
--                        (32 hex chars, Guid "N"); only that attempt may record
--                        the outcome, and recording it clears both columns;
--   * claim_expires_at — the lease: once it has passed, the retry poll of any
--                        instance may claim the delivery again.
-- A delivery is in flight when claim_id IS NOT NULL AND claim_expires_at > now.
-- Its status stays 'Pending': the delivery API and its status values do not change.
--
-- No new index (design D3): the claim of the next due delivery runs on the
-- existing partial index ix_webhook_deliveries_pending, and the claim predicate
-- only filters the few rows in flight.
--
-- Additive, no backfill, rollback = drop the two columns. Existing rows have no
-- claim and stay claimable. Old code ignores the columns, so a row left claimed
-- is still 'Pending' and due for an old poll: nothing gets stuck. Idempotent
-- (IF NOT EXISTS).
-- =============================================================================

ALTER TABLE webhook_deliveries
    ADD COLUMN IF NOT EXISTS claim_id VARCHAR(32),
    ADD COLUMN IF NOT EXISTS claim_expires_at TIMESTAMPTZ;
