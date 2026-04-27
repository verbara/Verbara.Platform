#!/usr/bin/env bash
# restore-pg.sh — Full restore of a Postgres database from a pg_dump custom-format file.
#
# Usage:
#   ./restore-pg.sh <backup-file>
#
# Env vars (all optional):
#   PG_HOST     default: localhost
#   PG_PORT     default: 5432
#   PG_USER     default: postgres
#   PG_DB       default: asterisk_platform   (DESTRUCTIVE — this DB will be dropped + recreated)
#   PGPASSWORD  not defaulted — set in env or via ~/.pgpass
#
# Safety: the script prompts for the database name and refuses to proceed unless
# typed exactly. There is no --force flag (intentional — see runbook §4.3 for context).
#
# Exit codes:
#   0  success
#   1  pg_restore failed
#   2  bad usage (missing file)
#   3  user did not confirm
#   4  prerequisite missing

set -euo pipefail

PG_HOST="${PG_HOST:-localhost}"
PG_PORT="${PG_PORT:-5432}"
PG_USER="${PG_USER:-postgres}"
PG_DB="${PG_DB:-asterisk_platform}"

log() {
    echo "[$(date -u +'%Y-%m-%dT%H:%M:%SZ')] $*"
}

die() {
    log "ERROR: $*"
    exit "${2:-1}"
}

# --- Args ---
if [[ $# -ne 1 ]]; then
    echo "Usage: $0 <backup-file>" >&2
    echo "  Restores the file into PG_DB (default: asterisk_platform) on PG_HOST." >&2
    echo "  PG_DB will be DROPPED and RECREATED." >&2
    exit 2
fi

BACKUP_FILE="$1"

# --- Prerequisites ---
command -v pg_restore >/dev/null 2>&1 || die "pg_restore not found in PATH" 4
command -v psql >/dev/null 2>&1       || die "psql not found in PATH" 4
[[ -f "$BACKUP_FILE" ]]               || die "Backup file not found: ${BACKUP_FILE}" 2

log "Restore plan:"
log "  Backup file : ${BACKUP_FILE} ($(du -h "$BACKUP_FILE" | awk '{print $1}'))"
log "  Target host : ${PG_HOST}:${PG_PORT}"
log "  Target user : ${PG_USER}"
log "  Target DB   : ${PG_DB}    <-- WILL BE DROPPED + RECREATED"
echo ""
echo "This is DESTRUCTIVE. To proceed, type the database name (${PG_DB}) exactly:"
read -r CONFIRM

if [[ "$CONFIRM" != "$PG_DB" ]]; then
    die "Confirmation mismatch (got '${CONFIRM}', expected '${PG_DB}'). Aborting." 3
fi

# --- DROP + CREATE ---
log "Terminating existing connections to ${PG_DB}"
psql -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" -d postgres -v ON_ERROR_STOP=1 -c \
    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '${PG_DB}' AND pid <> pg_backend_pid();" \
    >/dev/null

log "Dropping ${PG_DB} (if exists)"
psql -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" -d postgres -v ON_ERROR_STOP=1 -c \
    "DROP DATABASE IF EXISTS ${PG_DB};"

log "Creating ${PG_DB}"
psql -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" -d postgres -v ON_ERROR_STOP=1 -c \
    "CREATE DATABASE ${PG_DB};"

# --- Restore ---
log "Restoring from ${BACKUP_FILE}"
if ! pg_restore \
        -h "$PG_HOST" \
        -p "$PG_PORT" \
        -U "$PG_USER" \
        -d "$PG_DB" \
        --no-owner \
        --no-acl \
        --verbose \
        "$BACKUP_FILE"; then
    die "pg_restore failed (exit non-zero)" 1
fi

# --- Sanity check ---
TABLE_COUNT="$(psql -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" -d "$PG_DB" -tAc \
    "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public';")"

log "Restore OK — ${TABLE_COUNT} tables in public schema"
log "Recommended next steps:"
log "  1. Spot-check row counts (see runbook §1.4 step 5)."
log "  2. Restart Platform.Api against this DB."
log "  3. Hit /health/ready and run a smoke test."
exit 0
