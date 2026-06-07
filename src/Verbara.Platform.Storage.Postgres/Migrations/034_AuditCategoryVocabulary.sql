-- 034_AuditCategoryVocabulary.sql — align audit_entries_category_check with the
-- categories the application code actually emits.
--
-- Migration 021 (R5.3 Phase A, ADR-0006) introduced a CHECK constraint on
-- audit_entries.category but pinned it to a vocabulary that drifted from the
-- IAuditService.RecordAsync call sites. The code emits 'conversations',
-- 'queues', 'reports', 'operational' and 'license' — none of which were in the
-- 021 list — so every such audit write failed with 23514 on Postgres
-- deployments (silently swallowed by the defensive try/catch around each
-- RecordAsync, so the operation succeeded but the audit row was lost).
--
-- The v2.9.0 Session/Auth W3-W6 workers (AgentLivenessReaper force-offline,
-- PendingPauseDrainWorker, WorkFailoverWorker, CallbackRescueWorker, supervisor
-- reassign) widened the 'queues'/'conversations' emitters and surfaced this.
--
-- Fix: widen the constraint to the union of the historical 021 set and the
-- categories the code emits. Additive and safe — every existing row is already
-- 'config' (or another previously-valid value), so the re-ADD cannot fail.
ALTER TABLE audit_entries DROP CONSTRAINT IF EXISTS audit_entries_category_check;
ALTER TABLE audit_entries
    ADD CONSTRAINT audit_entries_category_check
    CHECK (category IN ('auth', 'billing', 'config', 'tenant', 'security',
                        'impersonation', 'retention', 'data', 'rbac',
                        'data_access', 'admin', 'conversations', 'queues',
                        'reports', 'operational', 'license'));
