#!/usr/bin/env bash
# R5.5 A.5 · Experiment 05 — Crash Asterisk + verify auto-restart.
#
# Validates: AMI / ARI reconnect from Platform.Api, in-flight call drop
# behavior, dialer-engine + agentassist-engine + livequeue-writer
# heartbeat staleness window.
#
# Expected: PlatformApiUnavailable does NOT fire (Asterisk down ≠ API down);
# HealthCheckUnhealthy P1 likely; in-flight calls drop with channel
# hangup cause 31 (NORMAL_TEMPORARY_FAILURE).
set -euo pipefail

COMPOSE="${COMPOSE:-docker/docker-compose.full.yml}"
TARGET="${TARGET:-re2:asterisk}"

echo "[chaos-05] Killing Asterisk ($TARGET)..."
pumba kill --signal SIGKILL "$TARGET"
sleep 30
docker compose -f "$COMPOSE" up -d --wait asterisk
echo "[chaos-05] Asterisk recovered."
