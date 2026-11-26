-- =============================================================================
-- Verbara.Platform — where the mail service stopped reading each reply mailbox (034)
-- =============================================================================
-- csat-email-replies-forwarded-once (#696).
--
-- The mail service reads each CSAT reply mailbox and forwards every reply to
-- the Api. It kept the UID of the last message it handled only in memory, so
-- after a restart it read the whole mailbox again and forwarded every reply a
-- second time. This table keeps that position per mailbox, with the mailbox's
-- UIDVALIDITY: when the server renumbers the mailbox the stored UID no longer
-- means anything and the mail service starts again from the newest message.
--
-- One row per configured mailbox (tenant, host, port, login, folder). The mail
-- service writes the row after each message it handles. No row means the mail
-- service has not read that mailbox since this migration: its first read takes
-- the newest message as the starting point and forwards no older one.
--
-- Locks: CREATE TABLE only; no existing table is touched.
--
-- Idempotent: IF NOT EXISTS, so a second run leaves the same table.
-- Rollback: an older binary does not read the table and behaves as before.
-- To remove it, DROP TABLE IF EXISTS mail.imap_poll_positions;
-- =============================================================================

CREATE SCHEMA IF NOT EXISTS mail;

CREATE TABLE IF NOT EXISTS mail.imap_poll_positions (
    tenant_id     TEXT        NOT NULL,
    host          TEXT        NOT NULL,
    port          INTEGER     NOT NULL,
    username      TEXT        NOT NULL,
    folder        TEXT        NOT NULL,
    uid_validity  BIGINT      NOT NULL CHECK (uid_validity BETWEEN 0 AND 4294967295),
    last_uid      BIGINT      NOT NULL CHECK (last_uid BETWEEN 0 AND 4294967295),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, host, port, username, folder)
);
