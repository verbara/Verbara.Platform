#!/usr/bin/env bash
# Phase 6 AOT 24h soak drift logger — samples every 10 min.
set -u
OUT="$1"
API_CTR=docker-platform-api-1
PG_CTR=docker-postgres-1
{
  printf '  %sZ T0 baseline\n' "$(date -u +%H:%M:%S)"
  t0mem=$(docker stats --no-stream --format '{{.MemUsage}}' "$API_CTR" 2>/dev/null)
  printf 'T0 mem=%s\n' "$t0mem"
} >> "$OUT"
while true; do
  sleep 600
  ts=$(date -u +%H:%M:%SZ)
  mem=$(docker stats --no-stream --format '{{.MemUsage}}' "$API_CTR" 2>/dev/null | tr -d ' ')
  cpu=$(docker stats --no-stream --format '{{.CPUPerc}}' "$API_CTR" 2>/dev/null)
  conns=$(docker exec "$PG_CTR" psql -U loadtest -d verbara_loadtest -tA \
          -c "select count(*) from pg_stat_activity where datname='verbara_loadtest'" 2>/dev/null | tr -d '[:space:]')
  disk=$(df -BG / 2>/dev/null | awk 'NR==2{gsub("G","",$4);print $4}')
  printf '%s api[%s] cpu=%s pg_conns=%s disk_free=%sG\n' "$ts" "$mem" "$cpu" "$conns" "$disk" >> "$OUT"
done
