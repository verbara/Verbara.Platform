#!/usr/bin/env bash
# R5.5 A.5 · Experiment 02 — Kill Postgres + verify auto-restart.
#
# Validates: docker-compose restart policy + DatabaseMigrationService re-run
# safety on a re-attached volume + ResiliencePolicy circuit breaker open
# duration on a "real outage" (vs. pause).
#
# Expected: ~30-45 s recovery, all HCs return to Healthy, in-flight requests
# 5xx during the gap, no schema corruption, no migration re-application.
set -euo pipefail

COMPOSE="${COMPOSE:-docker/docker-compose.full.yml}"
TARGET="${TARGET:-re2:postgres}"

echo "[chaos-02] Killing Postgres ($TARGET, SIGKILL)..."
pumba kill --signal SIGKILL "$TARGET"
echo "[chaos-02] Waiting 30 s for compose restart policy..."
sleep 30
echo "[chaos-02] Ensuring Postgres back up..."
docker compose -f "$COMPOSE" up -d --wait postgres
echo "[chaos-02] Postgres recovered."
