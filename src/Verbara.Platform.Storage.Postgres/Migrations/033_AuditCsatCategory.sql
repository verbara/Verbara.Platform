-- =============================================================================
-- Verbara.Platform — the csat audit category (033)
-- =============================================================================
-- audit-categories-accepted (#694).
--
-- Every CSAT capture (webchat, email, SMS, voice) and every CSAT template change
-- writes an audit record with the category csat. The category check did not list
-- csat, so each of those writes failed after the rating or template was saved,
-- and the capture answered 500. This widens the check by that one value and keeps
-- every value it accepted before.
--
-- Locks (the runner wraps the file in one transaction): ADD CONSTRAINT ... CHECK
-- takes an ACCESS EXCLUSIVE lock on audit_entries and scans it once to validate
-- the existing rows, which all hold a value of the old list.
--
-- Idempotent: the constraint is dropped if present and added again, so a second
-- run leaves the same check. Rollback: an older binary still writes csat and
-- fails as before. To restore the old list (only once no row holds csat),
--   ALTER TABLE audit_entries DROP CONSTRAINT IF EXISTS audit_entries_category_check;
--   ALTER TABLE audit_entries ADD CONSTRAINT audit_entries_category_check
--     CHECK (category IN ('auth', 'billing', 'config', 'tenant', 'security',
--                         'impersonation', 'retention', 'data', 'rbac',
--                         'data_access', 'admin', 'conversations', 'queues',
--                         'reports', 'operational', 'license', 'contact'));
-- =============================================================================

ALTER TABLE audit_entries
    DROP CONSTRAINT IF EXISTS audit_entries_category_check;

ALTER TABLE audit_entries
    ADD CONSTRAINT audit_entries_category_check
        CHECK (category IN ('auth', 'billing', 'config', 'tenant', 'security',
                            'impersonation', 'retention', 'data', 'rbac',
                            'data_access', 'admin', 'conversations', 'queues',
                            'reports', 'operational', 'license', 'contact',
                            'csat'));
