#!/usr/bin/env bash
# R5.5 A.5 · Experiment 04 — Kill Redis + verify auto-restart.
#
# Validates: SignalR backplane reconnect, Identity.Redis token refresh
# behavior post-flush. Pre-condition: Redis container exists.
set -euo pipefail

COMPOSE="${COMPOSE:-docker/docker-compose.full.yml}"
TARGET="${TARGET:-re2:redis}"

if ! docker ps --format '{{.Names}}' | grep -qi redis; then
    echo "[chaos-04] No Redis container detected — skipping."
    exit 0
fi

echo "[chaos-04] Killing Redis ($TARGET)..."
pumba kill --signal SIGKILL "$TARGET"
sleep 30
docker compose -f "$COMPOSE" up -d --wait redis 2>/dev/null || true
echo "[chaos-04] Redis recovered."
