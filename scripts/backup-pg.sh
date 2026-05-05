#!/usr/bin/env bash
# backup-pg.sh — Daily Postgres backup with optional S3 upload + 30-day local retention.
#
# Cron usage (host crontab):
#   0 2 * * *  /opt/verbara-platform/scripts/backup-pg.sh >> /var/log/verbara-platform/backup-pg.log 2>&1
#
# Env vars (all optional):
#   PG_HOST     default: localhost
#   PG_PORT     default: 5432
#   PG_USER     default: postgres
#   PG_DB       default: asterisk_platform
#   PGPASSWORD  not defaulted — set in cron env or via ~/.pgpass
#   BACKUP_DIR  default: /var/backups/pg
#   S3_BUCKET   if set, also uploads to s3://$S3_BUCKET/pg/<filename>
#   RETENTION_DAYS  default: 30 (local files older than this are deleted)
#
# Exit codes:
#   0  success
#   1  pg_dump failed
#   2  S3 upload failed (when S3_BUCKET set)
#   3  prerequisite missing (pg_dump or aws CLI)

set -euo pipefail

PG_HOST="${PG_HOST:-localhost}"
PG_PORT="${PG_PORT:-5432}"
PG_USER="${PG_USER:-postgres}"
PG_DB="${PG_DB:-asterisk_platform}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/pg}"
RETENTION_DAYS="${RETENTION_DAYS:-30}"

log() {
    echo "[$(date -u +'%Y-%m-%dT%H:%M:%SZ')] $*"
}

die() {
    log "ERROR: $*"
    exit "${2:-1}"
}

# --- Prerequisites ---
command -v pg_dump >/dev/null 2>&1 || die "pg_dump not found in PATH" 3
if [[ -n "${S3_BUCKET:-}" ]]; then
    command -v aws >/dev/null 2>&1 || die "aws CLI not found in PATH (S3_BUCKET is set)" 3
fi

# --- Prepare output dir + filename ---
mkdir -p "$BACKUP_DIR"
TIMESTAMP="$(date -u +'%Y%m%d-%H%M%S')"
BACKUP_FILE="${BACKUP_DIR}/${PG_DB}-${TIMESTAMP}.dump"

log "Starting backup: host=${PG_HOST} db=${PG_DB} target=${BACKUP_FILE}"

# --- Dump ---
# -F custom -Z 9 = compressed binary format (~70-90% smaller than plain SQL).
# Restore via pg_restore (see scripts/restore-pg.sh).
if ! pg_dump \
        -h "$PG_HOST" \
        -p "$PG_PORT" \
        -U "$PG_USER" \
        -d "$PG_DB" \
        -F custom \
        -Z 9 \
        --no-owner \
        --no-acl \
        -f "$BACKUP_FILE"; then
    die "pg_dump failed for ${PG_DB} on ${PG_HOST}" 1
fi

BACKUP_SIZE="$(du -h "$BACKUP_FILE" | awk '{print $1}')"
log "Dump complete: ${BACKUP_FILE} (${BACKUP_SIZE})"

# --- Optional S3 upload ---
if [[ -n "${S3_BUCKET:-}" ]]; then
    S3_KEY="s3://${S3_BUCKET}/pg/${PG_DB}-${TIMESTAMP}.dump"
    log "Uploading to ${S3_KEY}"
    if ! aws s3 cp "$BACKUP_FILE" "$S3_KEY" --only-show-errors; then
        die "S3 upload failed: ${S3_KEY}" 2
    fi
    log "S3 upload OK"
else
    log "S3_BUCKET not set — skipping S3 upload"
fi

# --- Local retention prune ---
log "Pruning local backups older than ${RETENTION_DAYS} days from ${BACKUP_DIR}"
PRUNED_COUNT="$(find "$BACKUP_DIR" -maxdepth 1 -name '*.dump' -type f -mtime "+${RETENTION_DAYS}" -print -delete | wc -l)"
log "Pruned ${PRUNED_COUNT} old backup file(s)"

log "Backup OK"
exit 0
