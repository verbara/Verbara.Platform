#!/usr/bin/env bash
# scripts/scenario-sweep.sh — R5.5 Phase C-L per-scenario rate sweep harness.
#
# Generalises scripts/jwt-sweep.sh: runs ONE NBomber scenario in isolation
# at a ladder of increasing rates (or VU counts for the VU-shaped Presence
# scenario), one rate per dotnet run, with a cooldown between steps. Each
# step produces its own NBomber report under
# tests/Verbara.Platform.LoadTests/load-test-reports/.
#
# Why per-scenario sweeps:
# - The R5.4 default suite runs all 5 scenarios in parallel at design rates,
#   producing a saturation snapshot but no per-endpoint knee.
# - A sequential ladder isolates each endpoint's curve so a single 5xx onset
#   point or p99 cliff is unambiguous.
#
# Usage:
#   ./scripts/scenario-sweep.sh <scenario> [rates...]
#
# Scenarios:
#   jwt          POST /api/v1/auth/login + /me           (rate-based)
#   queues       GET  /api/v1/admin/queues               (rate-based)
#   livequeue    GET  /api/v1/analytics/live/{queue}     (rate-based)
#   agentassist  GET  /api/v1/admin/teams                (rate-based)
#   presence     GET  /api/v1/admin/agents               (VU-based)
#   all-reads    queues + livequeue + agentassist + presence (sequential)
#
# Per-scenario default rate ladders (override with positional args, or env
# var SCENARIO_SWEEP_RATES_<UPPER>=...):
#   jwt          10 50 100 250 500          req/s
#   queues       10 50 100 250 500          req/s
#   livequeue    50 100 250 500 1000        req/s
#   agentassist  10 50 100 250 500          req/s
#   presence     100 250 500 1000 1500      VU count
#
# Pre-conditions:
# - docker-compose.full.yml + docker-compose.observability.yml stacks up
# - docker/.staging-admin-token populated (run scripts/seed-staging.sh first)
#
# Token freshness: the platform admin JWT lifetime is 15 minutes by default,
# while a full per-scenario sweep runs ~5.5 min and `all-reads` runs ~22 min.
# To guarantee no Unauthorized noise mid-sweep, this script refreshes the
# admin token via `/auth/login` BEFORE every step. Cost: one extra POST per
# step (~50 ms). The refreshed token is rewritten to docker/.staging-admin-
# token so subsequent invocations stay in sync.
#
# Output:
# - One NBomber report directory per step under
#   tests/Verbara.Platform.LoadTests/load-test-reports/
# - Per-step screen log at /tmp/scenario-sweep-<scenario>-r<rate>.log
#
# Env knobs:
#   PLATFORM_API_URL                default http://localhost:5000
#   ADMIN_EMAIL                     default platform-admin@r55-staging.local
#   ADMIN_PASSWORD                  default PlatformAdmin2026!
#   SCENARIO_SWEEP_DURATION_SEC     default 60
#   SCENARIO_COOLDOWN_SEC           default 5

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"

PLATFORM_API_URL="${PLATFORM_API_URL:-http://localhost:5000}"
ADMIN_EMAIL="${ADMIN_EMAIL:-platform-admin@r55-staging.local}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-PlatformAdmin2026!}"
SWEEP_DURATION="${SCENARIO_SWEEP_DURATION_SEC:-60}"
COOLDOWN="${SCENARIO_COOLDOWN_SEC:-5}"

# Refresh platform admin JWT via /auth/login. Writes to docker/.staging-admin-
# token + echoes to stdout. Exits non-zero on login failure.
refresh_admin_token() {
    local resp token
    resp=$(curl -fsS -X POST "$PLATFORM_API_URL/api/v1/auth/login" \
        -H "Content-Type: application/json" \
        -H "X-Tenant-Id: platform" \
        -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\"}" 2>/dev/null) || {
        echo "[scenario-sweep] FAIL: /auth/login returned non-2xx for $ADMIN_EMAIL." >&2
        exit 5
    }
    token=$(echo "$resp" | jq -r '.accessToken // empty')
    if [ -z "$token" ] || [ "$token" = "null" ]; then
        echo "[scenario-sweep] FAIL: /auth/login response missing accessToken." >&2
        echo "[scenario-sweep]       Body: $resp" >&2
        exit 6
    fi
    printf '%s' "$token" > "$ROOT/docker/.staging-admin-token"
    chmod 600 "$ROOT/docker/.staging-admin-token"
    printf '%s' "$token"
}

usage() {
    grep -E '^# ' "$0" | sed 's/^# //'
    exit 2
}

[ $# -ge 1 ] || usage

scenario="$1"
shift || true

# Map scenario alias -> LOADTEST_MODE + env var name + default ladder.
case "$scenario" in
    jwt)
        mode="jwt-only";          ladder_env="LOADTEST_RATE"; default_ladder="10 50 100 250 500" ;;
    queues)
        mode="queues-only";       ladder_env="LOADTEST_RATE"; default_ladder="10 50 100 250 500" ;;
    livequeue)
        mode="livequeue-only";    ladder_env="LOADTEST_RATE"; default_ladder="50 100 250 500 1000" ;;
    agentassist)
        mode="agentassist-only";  ladder_env="LOADTEST_RATE"; default_ladder="10 50 100 250 500" ;;
    presence)
        mode="presence-only";     ladder_env="LOADTEST_VU";   default_ladder="100 250 500 1000 1500" ;;
    all-reads)
        # Recurse: 4 scenarios (skip jwt — driven separately by jwt-sweep.sh).
        echo "[scenario-sweep] all-reads — running queues, livequeue, agentassist, presence sequentially."
        for s in queues livequeue agentassist presence; do
            echo ""
            echo "[scenario-sweep] ============================================="
            echo "[scenario-sweep] === scenario: $s ==="
            echo "[scenario-sweep] ============================================="
            "$0" "$s" "$@"
        done
        echo ""
        echo "[scenario-sweep] all-reads DONE."
        exit 0
        ;;
    -h|--help|help|"")
        usage ;;
    *)
        echo "[scenario-sweep] FAIL: unknown scenario '$scenario'." >&2
        usage ;;
esac

# Positional args after scenario override the ladder; env override beats both.
upper_scenario="$(echo "$scenario" | tr '[:lower:]' '[:upper:]')"
env_override_var="SCENARIO_SWEEP_RATES_${upper_scenario}"
env_override="${!env_override_var:-}"

if [ -n "$env_override" ]; then
    ladder="$env_override"
elif [ $# -gt 0 ]; then
    ladder="$*"
else
    ladder="$default_ladder"
fi

# Pre-flight checks.
if ! curl -fsS -o /dev/null "$PLATFORM_API_URL/health"; then
    echo "[scenario-sweep] FAIL: $PLATFORM_API_URL/health unreachable." >&2
    echo "[scenario-sweep]       Bring up docker-compose.full.yml first:" >&2
    echo "[scenario-sweep]       docker compose -f docker/docker-compose.full.yml up -d --wait" >&2
    exit 3
fi

command -v jq >/dev/null 2>&1 || {
    echo "[scenario-sweep] FAIL: missing dependency: jq" >&2
    exit 4
}

echo "[scenario-sweep] Scenario:     $scenario (mode=$mode)"
echo "[scenario-sweep] Ladder var:   $ladder_env"
echo "[scenario-sweep] Ladder:       $ladder"
echo "[scenario-sweep] Per-step:     ${SWEEP_DURATION}s execution + ${COOLDOWN}s cooldown"
echo "[scenario-sweep] Target URL:   $PLATFORM_API_URL"
echo "[scenario-sweep] Admin login:  $ADMIN_EMAIL"

cd "$ROOT/tests/Verbara.Platform.LoadTests"
dotnet build -c Release --nologo > /dev/null

for step in $ladder; do
    echo ""
    echo "[scenario-sweep] === step ${ladder_env}=${step} × ${SWEEP_DURATION}s ==="
    ADMIN_TOKEN=$(refresh_admin_token)
    log="/tmp/scenario-sweep-${scenario}-r${step}.log"
    env \
        LOADTEST_MODE="$mode" \
        LOADTEST_TENANT=medium-loadtest \
        LOADTEST_TOKEN="$ADMIN_TOKEN" \
        LOADTEST_DURATION_SEC="$SWEEP_DURATION" \
        PLATFORM_API_URL="$PLATFORM_API_URL" \
        "$ladder_env=$step" \
        dotnet run -c Release --no-build > "$log" 2>&1
    grep -E "ok count:|fail count:|p99 =|status code|^│ +Unauthorized|^│ +OK +│|^│ +InternalServerError|^│ +-101" "$log" | head -15 || true
    echo "[scenario-sweep] Step ${ladder_env}=${step} log: $log"
    sleep "$COOLDOWN"
done

echo ""
echo "[scenario-sweep] DONE. Per-step logs in /tmp/scenario-sweep-${scenario}-*.log."
echo "[scenario-sweep] NBomber reports under tests/Verbara.Platform.LoadTests/load-test-reports/."
