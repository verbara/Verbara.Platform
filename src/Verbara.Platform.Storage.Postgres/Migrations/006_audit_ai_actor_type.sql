-- Migration 006: extend audit_entries actor_type CHECK to include 'ai'.
-- Required for GDPR Art. 22 AI-disposition audit entries (B4b typification task).
-- The 'ai' actor type is used when an AI classifier decision is recorded as provenance
-- in an audit entry. In phase D the human agent is still the actor; 'ai' is reserved
-- for the future autonomous typification worker (task E5).

ALTER TABLE audit_entries
    DROP CONSTRAINT IF EXISTS audit_entries_actor_type_check;

ALTER TABLE audit_entries
    ADD CONSTRAINT audit_entries_actor_type_check
        CHECK (actor_type IN ('user', 'system', 'impersonator', 'service-account', 'api_key', 'ai'));
