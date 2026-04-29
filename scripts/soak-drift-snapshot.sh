#!/usr/bin/env bash
# scripts/soak-drift-snapshot.sh — R5.5 Phase D-L hourly drift capture.
#
# Captures the metrics required by Phase D-L Task D-L.2 ("Monitor drift
# hourly") and appends them as a row to a CSV file. Default cadence is
# 1 hour; default output is dated under the LoadTests reports directory.
#
# Metrics captured per row:
#   - timestamp           (ISO 8601, local)
#   - api_rss_mb          (docker stats — platform-api working set)
#   - api_cpu_pct         (docker stats — platform-api CPU%)
#   - pg_rss_mb           (docker stats — postgres working set)
#   - pg_conns            (psql pg_stat_activity count, datname=platform)
#   - p99_latency_ms      (PromQL histogram_quantile over 5 min window)
#   - rps                 (PromQL rate over 1 min window, all routes)
#   - kestrel_conns       (PromQL sum of kestrel_active_connections)
#   - disk_free_gb        (df -BG / on host)
#   - prom_tsdb_mb        (du /prometheus inside r55-prometheus)
#
# Usage:
#   ./scripts/soak-drift-snapshot.sh                          # 1h loop
#   ./scripts/soak-drift-snapshot.sh --once                   # single row
#   ./scripts/soak-drift-snapshot.sh --interval-sec 1800      # 30 min
#   ./scripts/soak-drift-snapshot.sh --output /tmp/drift.csv  # explicit path
#
# Background launch (24h soak, survives shell exit):
#   nohup ./scripts/soak-drift-snapshot.sh \
#     > /tmp/soak-drift-snapshot.log 2>&1 & disown
#
# Default output:
#   tests/Asterisk.Platform.LoadTests/soak-reports/soak-drift-<DATE>.csv
#   (NOT load-test-reports/ — NBomber wipes that directory at the start of
#   every run, which would delete drift rows between snapshots.)
#
# References:
#   - docs/plans/active/2026-04-27-r5.5-execution-plan.md § D-L.2
#   - Companion of scripts/soak-log-watchdog.sh (disk-fill guard)

set -euo pipefail

PROM_URL="${PROM_URL:-http://localhost:9090}"
INTERVAL_SEC=3600
ONCE=0
DEFAULT_OUTPUT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/tests/Asterisk.Platform.LoadTests/soak-reports/soak-drift-$(date '+%Y-%m-%d').csv"
OUTPUT="$DEFAULT_OUTPUT"

while [ $# -gt 0 ]; do
    case "$1" in
        --interval-sec) INTERVAL_SEC="$2"; shift 2 ;;
        --output)       OUTPUT="$2"; shift 2 ;;
        --once)         ONCE=1; shift ;;
        -h|--help)
            grep -E '^# ' "$0" | sed 's/^# //'
            exit 0 ;;
        *)
            echo "[soak-drift-snapshot] FAIL: unknown arg '$1'" >&2
            exit 2 ;;
    esac
done

ts() { date '+%Y-%m-%d %H:%M:%S %z'; }
iso() { date '+%Y-%m-%dT%H:%M:%S%:z'; }

prom() {
    # PromQL instant query, return scalar value or empty.
    local q="$1"
    curl -fsS --get --data-urlencode "query=$q" "$PROM_URL/api/v1/query" 2>/dev/null \
        | jq -r '.data.result[0].value[1] // empty' 2>/dev/null || true
}

docker_stats() {
    # Capture stats once for the two containers we care about.
    docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}' \
        docker-platform-api-1 docker-postgres-1 2>/dev/null
}

mb_of() { awk -v s="$1" 'BEGIN{
    if (s ~ /GiB$/)      { sub(/GiB$/, "", s); printf "%.2f", s*1024 }
    else if (s ~ /MiB$/) { sub(/MiB$/, "", s); printf "%.2f", s }
    else if (s ~ /KiB$/) { sub(/KiB$/, "", s); printf "%.4f", s/1024 }
    else                 { printf "%s", s }
}'; }

snapshot_row() {
    local stats api_rss api_cpu pg_rss pg_conns p99_s p99_ms rps kestrel disk_free prom_tsdb
    stats=$(docker_stats)

    local api_line pg_line
    api_line=$(echo "$stats" | grep '^docker-platform-api-1 ' | head -1)
    pg_line=$(echo "$stats" | grep '^docker-postgres-1 ' | head -1)

    api_cpu=$(echo "$api_line" | awk '{gsub("%","",$2); print $2}')
    api_rss=$(mb_of "$(echo "$api_line" | awk '{print $3}')")
    pg_rss=$(mb_of "$(echo "$pg_line" | awk '{print $3}')")

    pg_conns=$(docker exec docker-postgres-1 psql -U platform -d platform -tA \
        -c "SELECT count(*) FROM pg_stat_activity WHERE datname='platform';" \
        2>/dev/null | tr -d '[:space:]') || pg_conns=""

    p99_s=$(prom 'histogram_quantile(0.99, sum by (le) (rate(http_server_request_duration_seconds_bucket[5m])))')
    p99_ms=$(awk -v v="${p99_s:-0}" 'BEGIN{printf "%.2f", v*1000}')

    rps=$(prom 'sum(rate(http_server_request_duration_seconds_count[1m]))')
    rps=$(awk -v v="${rps:-0}" 'BEGIN{printf "%.0f", v}')

    kestrel=$(prom 'sum(kestrel_active_connections)')
    kestrel=$(awk -v v="${kestrel:-0}" 'BEGIN{printf "%.0f", v}')

    disk_free=$(df -BG / 2>/dev/null | awk 'NR==2 {gsub("G","",$4); print $4}')

    prom_tsdb=$(docker exec r55-prometheus du -sm /prometheus 2>/dev/null | awk '{print $1}') || prom_tsdb=""

    printf '%s,%s,%s,%s,%s,%s,%s,%s,%s,%s\n' \
        "$(iso)" \
        "${api_rss:-}" "${api_cpu:-}" "${pg_rss:-}" "${pg_conns:-}" \
        "${p99_ms:-}" "${rps:-}" "${kestrel:-}" "${disk_free:-}" "${prom_tsdb:-}"
}

ensure_header() {
    if [ ! -f "$OUTPUT" ]; then
        mkdir -p "$(dirname "$OUTPUT")"
        echo "timestamp,api_rss_mb,api_cpu_pct,pg_rss_mb,pg_conns,p99_ms,rps,kestrel_conns,disk_free_gb,prom_tsdb_mb" \
            > "$OUTPUT"
    fi
}

run_once() {
    ensure_header
    local row
    row=$(snapshot_row)
    echo "$row" >> "$OUTPUT"
    echo "[$(ts)] drift snapshot → $OUTPUT"
    echo "          $row"
}

if [ "$ONCE" -eq 1 ]; then
    run_once
    exit 0
fi

echo "[$(ts)] drift-snapshot start: interval=${INTERVAL_SEC}s output=$OUTPUT"
ensure_header
while true; do
    run_once
    sleep "$INTERVAL_SEC"
done
