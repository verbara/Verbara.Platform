#!/usr/bin/env bash
# R5.5 A.5 · Experiment 06 — Crash Platform.Api + verify auto-restart.
#
# Validates: BackgroundService graceful shutdown vs. SIGKILL behavior,
# DataProtection key persistence (post-V022 fix), JWT / API-key
# revocation cache rebuild on cold start.
#
# Expected: PlatformApiUnavailable P0 fires after ~2 min;
# auto-restart from compose policy; HCs return Healthy < 60 s after
# container Up.
set -euo pipefail

COMPOSE="${COMPOSE:-docker/docker-compose.full.yml}"
TARGET="${TARGET:-re2:platform-api}"

echo "[chaos-06] Killing Platform.Api ($TARGET)..."
pumba kill --signal SIGKILL "$TARGET"
sleep 30
docker compose -f "$COMPOSE" up -d --wait platform-api
echo "[chaos-06] Platform.Api recovered."
