-- =============================================================================
-- Verbara.Platform — every role assignment names an existing user (024)
-- =============================================================================
-- add-user-roles-user-foreign-key (design D1-D4; decision_ref Platform/ADR-0037).
-- user_roles had a foreign key onto tenant_roles but none onto users, so it held
-- rows for user ids that match no user: rows written for an unknown id before the
-- role-assignment endpoints looked the user up, and the rows every hard-deleted
-- user left behind. Such a row is not inert: authorization re-reads user_roles for
-- a token's subject on every request, so a deleted user's unexpired access token
-- kept that role's permissions.
--
-- SELF-CLEANING. This migration deletes those orphan rows, then adds
--   user_roles (tenant_id, user_id) -> users (tenant_id, user_id) ON DELETE CASCADE
-- so the database refuses an orphan from here on (23503), and deleting a user
-- removes that user's role assignments in the same transaction. No manual step.
-- The removed rows are not kept. The RAISE NOTICE below gives the number removed
-- to an operator applying the file with psql; the runner does not log notices.
--
-- Locking (design D3): users and user_roles are locked SHARE ROW EXCLUSIVE for
-- the whole file — reads continue, writes wait — so no orphan can appear between
-- the clean-up and the key (ADD FOREIGN KEY takes the same lock on both). The
-- runner applies the file in one transaction, so NOT VALID + VALIDATE CONSTRAINT
-- would hold the same lock to commit and gains nothing; the validation is an
-- anti-join on the users primary key.
--
-- One DO block, so the steps are atomic even outside the runner's transaction.
-- Idempotent: a second run deletes nothing, and the key is added only when it is
-- missing.
-- =============================================================================

DO $$
DECLARE
    removed BIGINT;
BEGIN
    LOCK TABLE users, user_roles IN SHARE ROW EXCLUSIVE MODE;

    DELETE FROM user_roles ur
    WHERE NOT EXISTS (
        SELECT 1 FROM users u
        WHERE u.tenant_id = ur.tenant_id AND u.user_id = ur.user_id);
    GET DIAGNOSTICS removed = ROW_COUNT;
    RAISE NOTICE '024_UserRolesUserForeignKey: removed % role assignment(s) of users that do not exist', removed;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'user_roles'::regclass AND conname = 'user_roles_tenant_id_user_id_fkey')
    THEN
        ALTER TABLE user_roles
            ADD CONSTRAINT user_roles_tenant_id_user_id_fkey
            FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, user_id) ON DELETE CASCADE;
    END IF;
END
$$;
