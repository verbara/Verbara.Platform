#!/usr/bin/env bash
# scripts/chaos-test.sh — R5.5 A.5 Pumba chaos suite runner.
#
# Iterates the 10 experiments in tests/chaos-experiments/pumba/ sequentially,
# taking pre + post Postgres + Redis snapshots so each experiment's blast
# radius can be diff'd later. Used in Phase C-L on top of an active NBomber
# + SIPp baseline run.
#
# Env knobs:
#   COMPOSE          docker-compose file (default docker/docker-compose.full.yml)
#   PG_USER          Postgres superuser (default platform; must match POSTGRES_USER)
#   PG_DB            Postgres database (default platform)
#   RECOVERY_SLEEP   Seconds to wait between experiments (default 30)
#   SNAP_DIR         Override snapshot directory
#
# Exit code: 0 — runner always exits cleanly even if individual experiments
# fail mid-flight (recovery validation is the deliverable, not return code).

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"

COMPOSE="${COMPOSE:-$ROOT/docker/docker-compose.full.yml}"
EXP_DIR="$ROOT/tests/chaos-experiments/pumba"
SNAP_DIR="${SNAP_DIR:-$ROOT/chaos-snapshots/$(date +%Y%m%d-%H%M%S)}"
PG_USER="${PG_USER:-platform}"
PG_DB="${PG_DB:-platform}"
RECOVERY_SLEEP="${RECOVERY_SLEEP:-30}"

if ! command -v pumba >/dev/null 2>&1; then
    echo "[chaos] FAIL: pumba not found on PATH." >&2
    echo "[chaos]       Install with:" >&2
    echo "[chaos]         curl -sL https://github.com/alexei-led/pumba/releases/download/0.10.0/pumba_linux_amd64 -o /tmp/pumba" >&2
    echo "[chaos]         chmod +x /tmp/pumba && sudo mv /tmp/pumba /usr/local/bin/pumba" >&2
    exit 1
fi

mkdir -p "$SNAP_DIR"
echo "[chaos] Suite snapshot dir: $SNAP_DIR"
echo "[chaos] Compose:            $COMPOSE"
echo "[chaos] Recovery sleep:     ${RECOVERY_SLEEP}s"

snapshot() {
    local stage="$1"
    local pg_container redis_container
    pg_container=$(docker ps --filter name=postgres --format '{{.Names}}' | head -1)
    if [ -n "$pg_container" ]; then
        docker exec "$pg_container" pg_dumpall -U "$PG_USER" \
            > "$SNAP_DIR/$stage-pg.sql" 2>/dev/null || \
            echo "[chaos]   note: pg snapshot for $stage skipped (server unreachable?)"
    fi
    redis_container=$(docker ps --filter name=redis --format '{{.Names}}' | head -1)
    if [ -n "$redis_container" ]; then
        docker exec "$redis_container" redis-cli SAVE >/dev/null 2>&1 || true
        docker cp "$redis_container":/data/dump.rdb \
            "$SNAP_DIR/$stage-redis.rdb" 2>/dev/null || \
            echo "[chaos]   note: redis snapshot for $stage skipped"
    fi
}

count=0
for exp in "$EXP_DIR"/*.sh; do
    [ -f "$exp" ] || continue
    name=$(basename "$exp" .sh)
    count=$((count + 1))
    echo ""
    echo "[chaos] === [$count] $name ==="
    snapshot "${name}-pre"
    if "$exp"; then
        echo "[chaos]   ✓ $name script returned 0"
    else
        rc=$?
        echo "[chaos]   ⚠ $name returned $rc (some experiments are disruptive — check report)"
    fi
    echo "[chaos]   Sleeping ${RECOVERY_SLEEP}s for recovery..."
    sleep "$RECOVERY_SLEEP"
    snapshot "${name}-post"
done

echo ""
echo "[chaos] DONE — $count experiments executed."
echo "[chaos] Snapshots: $SNAP_DIR"
