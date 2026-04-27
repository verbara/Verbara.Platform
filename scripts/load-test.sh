#!/usr/bin/env bash
# scripts/load-test.sh — R5.4 S5.1 baseline load test driver.
#                       R5.5 A.2 amendment — staging profile + seed integration.
#
# Two profiles (selected via LOADTEST_PROFILE env var):
#
#   LOADTEST_PROFILE=fixture (default)
#     R5.4 self-contained path. Spins up docker/docker-compose.loadtest.yml
#     (Postgres + Redis + Asterisk + platform-api with `loadtest` tenant
#     seeded at boot via Loadtest__SeedTenant=true). PLATFORM_API_URL defaults
#     to http://localhost:8080. Tears the fixture down on exit.
#
#   LOADTEST_PROFILE=staging
#     R5.5 path — runs against the docker-compose.full.yml staging stack
#     populated by `scripts/seed-staging.sh`. PLATFORM_API_URL defaults to
#     http://localhost:5000. Does NOT bring the stack up or down — assumes
#     the operator already has docker-compose.full.yml + the observability
#     side-stack running. Logs in as agent1@<tenant>.local (seeded by
#     seed-staging.sh) so the NBomber load lands on a realistically-sized
#     tenant + benefits from /metrics + Prometheus scrape.
#
# Reports land in:
#   tests/Asterisk.Platform.LoadTests/load-test-reports/<timestamp>/
#
# Other env knobs:
#   LOADTEST_KEEP=1                — fixture profile only; leave stack up.
#   PLATFORM_API_URL=<url>         — override the per-profile default.
#   LOADTEST_TENANT=<id>           — staging profile only; default
#                                    medium-loadtest.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
PROFILE="${LOADTEST_PROFILE:-fixture}"

case "$PROFILE" in
    fixture)
        PLATFORM_API_URL="${PLATFORM_API_URL:-http://localhost:8080}"
        ;;
    staging)
        PLATFORM_API_URL="${PLATFORM_API_URL:-http://localhost:5000}"
        ;;
    *)
        echo "[load-test] FAIL: LOADTEST_PROFILE must be 'fixture' or 'staging' (got '$PROFILE')." >&2
        exit 2
        ;;
esac

if [ "$PROFILE" = "fixture" ]; then
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

    echo "[load-test] [fixture] Bringing up loadtest environment..."
    docker compose -f "$COMPOSE_FILE" up -d --wait

    echo "[load-test] [fixture] Logging in as seeded loadtest tenant..."
    LOADTEST_TOKEN=$(curl -fsS -X POST "$PLATFORM_API_URL/api/v1/auth/login" \
        -H "Content-Type: application/json" \
        -H "X-Tenant-Id: loadtest" \
        -d '{"email":"loadtest@loadtest.local","password":"loadtest"}' | jq -r '.accessToken // empty')
else
    LOADTEST_TENANT="${LOADTEST_TENANT:-medium-loadtest}"

    echo "[load-test] [staging] Targeting $PLATFORM_API_URL (tenant=$LOADTEST_TENANT)..."
    if ! curl -fsS -o /dev/null "$PLATFORM_API_URL/health"; then
        echo "[load-test] FAIL: $PLATFORM_API_URL/health unreachable." >&2
        echo "[load-test]       Bring up docker-compose.full.yml first:" >&2
        echo "[load-test]       docker compose -f docker/docker-compose.full.yml up -d --wait" >&2
        exit 3
    fi

    echo "[load-test] [staging] Ensuring tenants are seeded..."
    "$SCRIPT_DIR/seed-staging.sh" >/dev/null

    # Most non-JWT scenarios target Admin/SupervisorPlus-gated endpoints
    # (live analytics, queue admin, ...). Agent-role tokens 403 against
    # those, so the staging path uses the platform-admin token cached
    # at docker/.staging-admin-token (set by seed-staging.sh on first
    # bootstrap). JWT scenario logs in fresh inside its hot path with
    # the agent1 creds and is unaffected by this choice.
    if [ -f "$ROOT/docker/.staging-admin-token" ]; then
        LOADTEST_TOKEN=$(cat "$ROOT/docker/.staging-admin-token")
        echo "[load-test] [staging] Using cached platform-admin token for admin-gated scenarios."
    else
        echo "[load-test] [staging] Falling back to agent1 login (no cached admin token)..."
        LOADTEST_TOKEN=$(curl -fsS -X POST "$PLATFORM_API_URL/api/v1/auth/login" \
            -H "Content-Type: application/json" \
            -H "X-Tenant-Id: $LOADTEST_TENANT" \
            -d "{\"email\":\"agent1@${LOADTEST_TENANT}.local\",\"password\":\"Agent2026!\"}" | jq -r '.accessToken // empty')
    fi
fi

if [ -z "$LOADTEST_TOKEN" ] || [ "$LOADTEST_TOKEN" = "null" ]; then
    echo "[load-test] FAIL: could not obtain bearer token (profile=$PROFILE)." >&2
    exit 1
fi

echo "[load-test] Running NBomber suite (Release, profile=$PROFILE)..."
cd "$ROOT/tests/Asterisk.Platform.LoadTests"

# Pass tenant so all 5 scenarios add X-Tenant-Id + use seeded credentials.
# Fixture profile exports tenant=loadtest (legacy single-tenant fixture);
# staging exports the seeded tenant id (default medium-loadtest).
export_tenant="${LOADTEST_TENANT:-loadtest}"

PLATFORM_API_URL="$PLATFORM_API_URL" \
    LOADTEST_TOKEN="$LOADTEST_TOKEN" \
    LOADTEST_TENANT="$export_tenant" \
    dotnet run -c Release

echo "[load-test] Reports written to: $(pwd)/load-test-reports/"
