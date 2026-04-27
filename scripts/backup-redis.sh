#!/usr/bin/env bash
# backup-redis.sh — Trigger Redis BGSAVE, copy dump.rdb, optional S3 upload.
#
# Cron usage (host crontab):
#   */15 * * * *  /opt/asterisk-platform/scripts/backup-redis.sh >> /var/log/asterisk-platform/backup-redis.log 2>&1
#
# Env vars (all optional):
#   REDIS_HOST          default: localhost
#   REDIS_PORT          default: 6379
#   REDIS_PASSWORD      not defaulted — set if AUTH required
#   REDIS_CONTAINER     if set, copy dump via `docker cp <container>:/data/dump.rdb`
#                       (preferred when Redis runs in Docker — avoids host volume mount assumptions)
#   REDIS_RDB_PATH      default: /var/lib/redis/dump.rdb (used when REDIS_CONTAINER is unset)
#   BACKUP_DIR          default: /var/backups/redis
#   S3_BUCKET           if set, also uploads to s3://$S3_BUCKET/redis/<filename> + s3://$S3_BUCKET/redis/dump-latest.rdb
#   BGSAVE_TIMEOUT      default: 120 (seconds to wait for LASTSAVE to advance)
#   RETENTION_DAYS      default: 7  (local snapshots — 4 per hour × 24 × 7 = 672 files trimmed conservatively)
#
# Exit codes:
#   0  success
#   1  BGSAVE failed or LASTSAVE did not advance within timeout
#   2  copy / S3 upload failed
#   3  prerequisite missing

set -euo pipefail

REDIS_HOST="${REDIS_HOST:-localhost}"
REDIS_PORT="${REDIS_PORT:-6379}"
REDIS_RDB_PATH="${REDIS_RDB_PATH:-/var/lib/redis/dump.rdb}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/redis}"
BGSAVE_TIMEOUT="${BGSAVE_TIMEOUT:-120}"
RETENTION_DAYS="${RETENTION_DAYS:-7}"

log() {
    echo "[$(date -u +'%Y-%m-%dT%H:%M:%SZ')] $*"
}

die() {
    log "ERROR: $*"
    exit "${2:-1}"
}

# --- Prerequisites ---
command -v redis-cli >/dev/null 2>&1 || die "redis-cli not found in PATH" 3
if [[ -n "${S3_BUCKET:-}" ]]; then
    command -v aws >/dev/null 2>&1 || die "aws CLI not found in PATH (S3_BUCKET is set)" 3
fi
if [[ -n "${REDIS_CONTAINER:-}" ]]; then
    command -v docker >/dev/null 2>&1 || die "docker not found in PATH (REDIS_CONTAINER is set)" 3
fi

mkdir -p "$BACKUP_DIR"

# --- redis-cli wrapper that handles optional AUTH ---
rcli() {
    if [[ -n "${REDIS_PASSWORD:-}" ]]; then
        redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" -a "$REDIS_PASSWORD" --no-auth-warning "$@"
    else
        redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" "$@"
    fi
}

# --- Capture LASTSAVE timestamp before triggering BGSAVE ---
LASTSAVE_BEFORE="$(rcli LASTSAVE)"
if ! [[ "$LASTSAVE_BEFORE" =~ ^[0-9]+$ ]]; then
    die "LASTSAVE returned non-numeric: '${LASTSAVE_BEFORE}'" 1
fi
log "LASTSAVE before BGSAVE: ${LASTSAVE_BEFORE}"

# --- Trigger BGSAVE ---
log "Triggering BGSAVE on ${REDIS_HOST}:${REDIS_PORT}"
BGSAVE_REPLY="$(rcli BGSAVE)"
log "BGSAVE reply: ${BGSAVE_REPLY}"

# --- Wait for LASTSAVE to advance (confirms snapshot complete) ---
WAITED=0
while [[ "$WAITED" -lt "$BGSAVE_TIMEOUT" ]]; do
    sleep 2
    WAITED=$((WAITED + 2))
    LASTSAVE_NOW="$(rcli LASTSAVE)"
    if [[ "$LASTSAVE_NOW" -gt "$LASTSAVE_BEFORE" ]]; then
        log "LASTSAVE advanced to ${LASTSAVE_NOW} after ${WAITED}s"
        break
    fi
done

if [[ "$LASTSAVE_NOW" -le "$LASTSAVE_BEFORE" ]]; then
    die "LASTSAVE did not advance within ${BGSAVE_TIMEOUT}s — BGSAVE may have failed" 1
fi

# --- Copy dump.rdb out ---
TIMESTAMP="$(date -u +'%Y%m%d-%H%M%S')"
LOCAL_FILE="${BACKUP_DIR}/dump-${TIMESTAMP}.rdb"

if [[ -n "${REDIS_CONTAINER:-}" ]]; then
    log "Copying dump.rdb from container ${REDIS_CONTAINER}"
    if ! docker cp "${REDIS_CONTAINER}:/data/dump.rdb" "$LOCAL_FILE"; then
        die "docker cp failed" 2
    fi
else
    log "Copying ${REDIS_RDB_PATH} -> ${LOCAL_FILE}"
    if [[ ! -f "$REDIS_RDB_PATH" ]]; then
        die "RDB file not found at ${REDIS_RDB_PATH}" 2
    fi
    if ! cp "$REDIS_RDB_PATH" "$LOCAL_FILE"; then
        die "cp failed" 2
    fi
fi

LOCAL_SIZE="$(du -h "$LOCAL_FILE" | awk '{print $1}')"
log "Local snapshot saved: ${LOCAL_FILE} (${LOCAL_SIZE})"

# --- Optional S3 upload ---
if [[ -n "${S3_BUCKET:-}" ]]; then
    S3_DATED="s3://${S3_BUCKET}/redis/dump-${TIMESTAMP}.rdb"
    S3_LATEST="s3://${S3_BUCKET}/redis/dump-latest.rdb"
    log "Uploading to ${S3_DATED}"
    if ! aws s3 cp "$LOCAL_FILE" "$S3_DATED" --only-show-errors; then
        die "S3 upload failed: ${S3_DATED}" 2
    fi
    log "Updating ${S3_LATEST}"
    if ! aws s3 cp "$LOCAL_FILE" "$S3_LATEST" --only-show-errors; then
        die "S3 upload failed: ${S3_LATEST}" 2
    fi
    log "S3 uploads OK"
else
    log "S3_BUCKET not set — skipping S3 upload"
fi

# --- Local retention prune ---
log "Pruning local Redis snapshots older than ${RETENTION_DAYS} days from ${BACKUP_DIR}"
PRUNED_COUNT="$(find "$BACKUP_DIR" -maxdepth 1 -name 'dump-*.rdb' -type f -mtime "+${RETENTION_DAYS}" -print -delete | wc -l)"
log "Pruned ${PRUNED_COUNT} old snapshot file(s)"

log "Backup OK"
exit 0
