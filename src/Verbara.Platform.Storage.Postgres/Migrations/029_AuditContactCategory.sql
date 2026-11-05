-- =============================================================================
-- Verbara.Platform — the contact audit category (029)
-- =============================================================================
-- contacts-writes-follow-their-permissions (design D17).
--
-- From this release a change of a contact's do-not-contact or consent, and a
-- contact delete, write an audit record with the category contact, in the same
-- transaction as the contact change. The category check did not list contact,
-- so every such write would fail. This widens the check by that one value and
-- keeps every value it accepted before.
--
-- Locks (the runner wraps the file in one transaction): ADD CONSTRAINT ... CHECK
-- takes an ACCESS EXCLUSIVE lock on audit_entries and scans it once to validate
-- the existing rows, which all hold a value of the old list.
--
-- Idempotent: the constraint is dropped if present and added again, so a second
-- run leaves the same check. Rollback: an older binary never writes contact. To
-- restore the old list (only once no row holds contact),
--   ALTER TABLE audit_entries DROP CONSTRAINT IF EXISTS audit_entries_category_check;
--   ALTER TABLE audit_entries ADD CONSTRAINT audit_entries_category_check
--     CHECK (category IN ('auth', 'billing', 'config', 'tenant', 'security',
--                         'impersonation', 'retention', 'data', 'rbac',
--                         'data_access', 'admin', 'conversations', 'queues',
--                         'reports', 'operational', 'license'));
-- =============================================================================

ALTER TABLE audit_entries
    DROP CONSTRAINT IF EXISTS audit_entries_category_check;

ALTER TABLE audit_entries
    ADD CONSTRAINT audit_entries_category_check
        CHECK (category IN ('auth', 'billing', 'config', 'tenant', 'security',
                            'impersonation', 'retention', 'data', 'rbac',
                            'data_access', 'admin', 'conversations', 'queues',
                            'reports', 'operational', 'license', 'contact'));
