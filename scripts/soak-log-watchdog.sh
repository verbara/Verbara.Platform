#!/usr/bin/env bash
# scripts/soak-log-watchdog.sh — R5.5 Phase D-L log-rotation watchdog.
#
# Background guard for 24h soak runs against `docker-compose.smb.yml`.
# Default Docker `json-file` log driver has no size limit; under sustained
# high RPS (~10-11 k req/s presence sweep) the platform-api container log
# grows ~150 GB/hour and fills the host root fs in 2-3 hours.
#
# This watchdog runs in a loop, measures the json log file size of each
# running container via a privileged alpine probe (no host sudo needed),
# and truncates in place when any container crosses --threshold-gb. The
# truncate is non-destructive: the container keeps writing to the now-
# empty file, no process restart needed. Soak run is unaffected.
#
# Usage:
#   ./scripts/soak-log-watchdog.sh                        # loop, 10 min, 3 GB
#   ./scripts/soak-log-watchdog.sh --once                 # one check, then exit
#   ./scripts/soak-log-watchdog.sh --threshold-gb 5       # truncate over 5 GB
#   ./scripts/soak-log-watchdog.sh --interval-sec 300     # check every 5 min
#
# Background launch (survives shell exit, ~24h):
#   nohup ./scripts/soak-log-watchdog.sh \
#     > /tmp/soak-log-watchdog.log 2>&1 & disown
#
# References:
#   - docs/operations/alerts-runbook.md § NodeDiskSpaceLow (truncate procedure)
#   - commit 6146534 (runbook expansion with container-log triage)
#   - commit 8042d7d (NodeDiskSpaceLow P0 alert as safety net)

set -euo pipefail

THRESHOLD_GB=3
INTERVAL_SEC=600
ONCE=0

while [ $# -gt 0 ]; do
    case "$1" in
        --threshold-gb)   THRESHOLD_GB="$2"; shift 2 ;;
        --interval-sec)   INTERVAL_SEC="$2"; shift 2 ;;
        --once)           ONCE=1; shift ;;
        -h|--help)
            grep -E '^# ' "$0" | sed 's/^# //'
            exit 0 ;;
        *)
            echo "[soak-log-watchdog] FAIL: unknown arg '$1'" >&2
            exit 2 ;;
    esac
done

THRESHOLD_BYTES=$((THRESHOLD_GB * 1024 * 1024 * 1024))

ts() { date '+%Y-%m-%d %H:%M:%S %z'; }

tick() {
    # One privileged alpine probe per tick: scan every json log under
    # /var/lib/docker/containers/ read-only, emit "<id> <size>" lines.
    # Truncation is a second targeted probe, only for oversize files.
    # No host sudo required.

    local report
    report=$(docker run --rm --privileged \
        -v /var/lib/docker:/host_docker:ro \
        alpine:3.19 sh -c '
            for f in /host_docker/containers/*/*-json.log; do
                [ -f "$f" ] || continue
                id=$(basename "$f" -json.log)
                size=$(stat -c %s "$f" 2>/dev/null || echo 0)
                printf "%s %s\n" "$id" "$size"
            done
        ' 2>/dev/null) || true

    [ -z "$report" ] && {
        echo "[$(ts)] WARN: probe returned empty (docker daemon down?)"
        return
    }

    while IFS=' ' read -r id size; do
        [ -z "$id" ] && continue
        [ -z "$size" ] && continue
        if [ "$size" -gt "$THRESHOLD_BYTES" ]; then
            local size_gb name
            size_gb=$(awk -v s="$size" 'BEGIN{printf "%.2f", s/1073741824}')
            name=$(docker inspect --format '{{.Name}}' "$id" 2>/dev/null | sed 's|^/||')
            docker run --rm --privileged \
                -v /var/lib/docker:/host_docker \
                alpine:3.19 truncate -s 0 \
                "/host_docker/containers/$id/$id-json.log" \
                && echo "[$(ts)] truncated $name (${size_gb} GB → 0); df / now $(df / | awk 'NR==2 {print $5" used, "$4" free"}')" \
                || echo "[$(ts)] FAIL: truncate $name (${size_gb} GB)"
        fi
    done <<< "$report"
}

if [ "$ONCE" -eq 1 ]; then
    tick
    exit 0
fi

echo "[$(ts)] watchdog start: threshold=${THRESHOLD_GB}GB interval=${INTERVAL_SEC}s"
while true; do
    tick
    sleep "$INTERVAL_SEC"
done
