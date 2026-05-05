-- Sprint 5: Audit impersonator tracking
ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS impersonator_id TEXT NULL;
