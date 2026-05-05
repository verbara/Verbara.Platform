#!/usr/bin/env bash
# scripts/jwt-sweep.sh — R5.5 Phase B-L JWT rate sweep.
#
# Runs JwtScenario standalone at 5 increasing rates (10/50/100/250/500
# req/s × 60 s each, sequentially) to identify the docker-compose
# single-instance saturation knee on the host. Each step produces its
# own NBomber report; per-step logs land in /tmp/jwt-sweep-r<rate>.log.
#
# Pre-conditions:
# - docker-compose.full.yml + docker-compose.observability.yml stacks up
#   (run scripts/load-test.sh once first to seed tenants).
# - docker/.staging-admin-token populated.
#
# Output:
# - One NBomber report directory per step under
#   tests/Verbara.Platform.LoadTests/load-test-reports/.
# - Per-step screen log at /tmp/jwt-sweep-r<rate>.log.
#
# Env knobs:
#   PLATFORM_API_URL       default http://localhost:5000
#   JWT_SWEEP_RATES        space-separated rates (default "10 50 100 250 500")
#   JWT_SWEEP_DURATION_SEC default 60

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"

PLATFORM_API_URL="${PLATFORM_API_URL:-http://localhost:5000}"
SWEEP_RATES="${JWT_SWEEP_RATES:-10 50 100 250 500}"
SWEEP_DURATION="${JWT_SWEEP_DURATION_SEC:-60}"

if ! curl -fsS -o /dev/null "$PLATFORM_API_URL/health"; then
    echo "[jwt-sweep] FAIL: $PLATFORM_API_URL/health unreachable." >&2
    echo "[jwt-sweep]       Bring up docker-compose.full.yml first:" >&2
    echo "[jwt-sweep]       docker compose -f docker/docker-compose.full.yml up -d --wait" >&2
    exit 3
fi

if [ ! -f "$ROOT/docker/.staging-admin-token" ]; then
    echo "[jwt-sweep] FAIL: docker/.staging-admin-token missing." >&2
    echo "[jwt-sweep]       Run ./scripts/seed-staging.sh first to bootstrap." >&2
    exit 4
fi

ADMIN_TOKEN=$(cat "$ROOT/docker/.staging-admin-token")

echo "[jwt-sweep] Sweep rates: $SWEEP_RATES (req/s)"
echo "[jwt-sweep] Per-step duration: ${SWEEP_DURATION}s"
echo "[jwt-sweep] Target URL: $PLATFORM_API_URL"

cd "$ROOT/tests/Verbara.Platform.LoadTests"
dotnet build -c Release --nologo > /dev/null

for rate in $SWEEP_RATES; do
    echo ""
    echo "[jwt-sweep] === step rate=${rate} req/s × ${SWEEP_DURATION}s ==="
    LOADTEST_MODE=jwt-only \
    LOADTEST_TENANT=medium-loadtest \
    LOADTEST_TOKEN="$ADMIN_TOKEN" \
    LOADTEST_RATE="$rate" \
    LOADTEST_DURATION_SEC="$SWEEP_DURATION" \
    PLATFORM_API_URL="$PLATFORM_API_URL" \
        dotnet run -c Release --no-build > "/tmp/jwt-sweep-r${rate}.log" 2>&1
    grep -E "ok count:|fail count:|p99 =|status code|^│ +OK +│|^│ +InternalServerError|^│ +-101" "/tmp/jwt-sweep-r${rate}.log" | head -15
    echo "[jwt-sweep] Step r=${rate} log: /tmp/jwt-sweep-r${rate}.log"
    sleep 5
done

echo ""
echo "[jwt-sweep] DONE. Per-step logs in /tmp/jwt-sweep-r{rate}.log."
echo "[jwt-sweep] NBomber reports under tests/Verbara.Platform.LoadTests/load-test-reports/."
