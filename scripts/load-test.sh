#!/usr/bin/env bash
# scripts/load-test.sh — R5.4 S5.1 baseline load test driver.
#
# Brings up docker/docker-compose.loadtest.yml (Postgres + Redis + Asterisk +
# platform-api preconfigured to seed a "loadtest" tenant), obtains a JWT for
# the seeded tenant, runs the NBomber suite, and tears the stack down.
#
# Reports land in:
#   tests/Asterisk.Platform.LoadTests/load-test-reports/<timestamp>/
#
# Set LOADTEST_KEEP=1 to leave the stack running after the suite completes
# (useful when iterating on scenarios). Set PLATFORM_API_URL to override the
# default http://localhost:8080 (e.g. when targeting a remote staging stack).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"

PLATFORM_API_URL="${PLATFORM_API_URL:-http://localhost:8080}"
COMPOSE_FILE="$ROOT/docker/docker-compose.loadtest.yml"

cleanup() {
    if [ "${LOADTEST_KEEP:-0}" = "1" ]; then
        echo "[load-test] LOADTEST_KEEP=1 set; leaving stack up."
        return
    fi
    echo "[load-test] Bringing down loadtest environment..."
    docker compose -f "$COMPOSE_FILE" down --volumes --remove-orphans
}
trap cleanup EXIT

echo "[load-test] Bringing up loadtest environment..."
docker compose -f "$COMPOSE_FILE" up -d --wait

echo "[load-test] Seeding loadtest tenant + obtaining bearer token..."
LOADTEST_TOKEN=$(curl -fsS -X POST "$PLATFORM_API_URL/api/v1/auth/login" \
    -H "Content-Type: application/json" \
    -d '{"username":"loadtest","password":"loadtest"}' | jq -r '.accessToken')

if [ -z "$LOADTEST_TOKEN" ] || [ "$LOADTEST_TOKEN" = "null" ]; then
    echo "[load-test] FAIL: could not obtain loadtest token. Is the loadtest tenant seeded?" >&2
    exit 1
fi

echo "[load-test] Running NBomber suite (Release)..."
cd "$ROOT/tests/Asterisk.Platform.LoadTests"
PLATFORM_API_URL="$PLATFORM_API_URL" \
    LOADTEST_TOKEN="$LOADTEST_TOKEN" \
    dotnet run -c Release

echo "[load-test] Reports written to: $(pwd)/load-test-reports/"
