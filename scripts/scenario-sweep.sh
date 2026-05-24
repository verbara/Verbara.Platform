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
# admin token via `/auth/login` BEFORE every step *that needs it*. Cost: zero
# extra POSTs when the cached token is still > TOKEN_STALENESS_SEC away from
# its exp claim; one POST otherwise. The refreshed token is rewritten to
# docker/.staging-admin-token so subsequent invocations stay in sync.
#
# Resilience: when the JwtScenario itself hammers /auth/login, the rate-limiter
# may transiently 503-throttle subsequent admin logins between steps. In that
# case refresh_admin_token falls back to the cached token if it has ANY
# remaining lifetime (logs a WARN). The sweep continues; tokens issued before
# the burst remain valid for their full TTL. R5.5 Phase B-LK 2026-05-24
# documented this failure mode and motivated the fallback.
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
#   TOKEN_STALENESS_SEC             default 60 — skip refresh if cached token
#                                   exp is more than this many seconds away

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"

PLATFORM_API_URL="${PLATFORM_API_URL:-http://localhost:5000}"
ADMIN_EMAIL="${ADMIN_EMAIL:-platform-admin@r55-staging.local}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-PlatformAdmin2026!}"
SWEEP_DURATION="${SCENARIO_SWEEP_DURATION_SEC:-60}"
COOLDOWN="${SCENARIO_COOLDOWN_SEC:-5}"

# Token-staleness threshold (seconds before exp). Default 60s headroom so the
# 60-second sweep step never races against access-token expiry. Override via
# env if the configured access-token TTL is shorter than 60s.
TOKEN_STALENESS_SEC="${TOKEN_STALENESS_SEC:-60}"

# Decode a JWT's `exp` claim (epoch seconds) without external dependencies.
# Returns 0 on a token whose exp is missing/unparseable so the caller treats
# it as expired (forces refresh).
jwt_exp_seconds() {
    local token="$1"
    local payload
    # JWT format: header.payload.signature — base64url-encoded payload, may
    # need padding before base64 -d. Tr swaps URL-safe chars; sed pads.
    payload=$(printf '%s' "$token" | awk -F. '{print $2}' | tr '_-' '/+' | sed -E 's/$/===/' | cut -c1-$((($(printf '%s' "$token" | awk -F. '{print $2}' | wc -c) + 3) / 4 * 4))) || return 0
    [ -z "$payload" ] && { echo 0; return 0; }
    echo "$payload" | base64 -d 2>/dev/null | jq -r '.exp // 0' 2>/dev/null || echo 0
}

# Check whether a cached token is still safe to reuse: present, parseable,
# and exp > now + TOKEN_STALENESS_SEC.
token_is_fresh() {
    local token="$1" exp now
    [ -z "$token" ] && return 1
    exp=$(jwt_exp_seconds "$token")
    [ "$exp" -lt 1 ] 2>/dev/null && return 1
    now=$(date -u +%s)
    [ $((exp - now)) -gt "$TOKEN_STALENESS_SEC" ]
}

# Refresh platform admin JWT via /auth/login. Writes to docker/.staging-admin-
# token + echoes to stdout.
#
# Resilience: if a cached token in docker/.staging-admin-token is still fresh
# (exp > now + TOKEN_STALENESS_SEC), reuse it WITHOUT hitting /auth/login.
# If a fresh refresh is needed but the network call fails (e.g., rate-limiter
# throttling /auth/login mid-sweep — R5.5 Phase B-LK 2026-05-24 JWT scenario
# self-DoS at rate=250), fall back to the cached token when it has ANY
# remaining lifetime. Only exits non-zero if BOTH the refresh AND the cached
# token are unusable.
refresh_admin_token() {
    local cached resp token cached_exp now remaining
    local cache_file="$ROOT/docker/.staging-admin-token"

    # ---- Fast path: cached token still fresh ----
    if [ -f "$cache_file" ]; then
        cached=$(cat "$cache_file")
        if token_is_fresh "$cached"; then
            printf '%s' "$cached"
            return 0
        fi
    fi

    # ---- Refresh path: try /auth/login ----
    resp=$(curl -fsS -X POST "$PLATFORM_API_URL/api/v1/auth/login" \
        -H "Content-Type: application/json" \
        -H "X-Tenant-Id: platform" \
        -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\"}" 2>/dev/null) || {
        # ---- Fallback path: refresh failed, reuse cached token if any life left ----
        if [ -n "${cached:-}" ]; then
            cached_exp=$(jwt_exp_seconds "$cached")
            now=$(date -u +%s)
            remaining=$((cached_exp - now))
            if [ "$remaining" -gt 0 ] 2>/dev/null; then
                echo "[scenario-sweep] WARN: /auth/login refresh failed, reusing cached token (${remaining}s remaining)." >&2
                printf '%s' "$cached"
                return 0
            fi
        fi
        echo "[scenario-sweep] FAIL: /auth/login returned non-2xx for $ADMIN_EMAIL AND no usable cached token." >&2
        exit 5
    }
    token=$(echo "$resp" | jq -r '.accessToken // empty')
    if [ -z "$token" ] || [ "$token" = "null" ]; then
        echo "[scenario-sweep] FAIL: /auth/login response missing accessToken." >&2
        echo "[scenario-sweep]       Body: $resp" >&2
        exit 6
    fi
    printf '%s' "$token" > "$cache_file"
    chmod 600 "$cache_file"
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
    # Preserve this step's report before NBomber's next run wipes it. Sibling
    # dir under load-test-reports/ named by scenario+ladder-value+duration.
    # Confirmed 2026-05-24: NBomber 6.x clears load-test-reports/ at run-start
    # (recursive), including any subdirs, so the preserve sibling must live
    # OUTSIDE load-test-reports/. See docs/operations/r55-blk-evidence/.
    preserve_dir="$ROOT/tests/Verbara.Platform.LoadTests/load-test-reports-archive/${scenario}-${ladder_env}-${step}-${SWEEP_DURATION}s"
    mkdir -p "$preserve_dir"
    mv load-test-reports/nbomber_report_*.{csv,html,md} "$preserve_dir/" 2>/dev/null || true
    mv load-test-reports/nbomber-log-*.txt "$preserve_dir/" 2>/dev/null || true
    grep -E "ok count:|fail count:|p99 =|status code|^│ +Unauthorized|^│ +OK +│|^│ +InternalServerError|^│ +-101" "$log" | head -15 || true
    echo "[scenario-sweep] Step ${ladder_env}=${step} log: $log"
    echo "[scenario-sweep] Step ${ladder_env}=${step} report: $preserve_dir"
    sleep "$COOLDOWN"
done

echo ""
echo "[scenario-sweep] DONE. Per-step logs in /tmp/scenario-sweep-${scenario}-*.log."
echo "[scenario-sweep] NBomber per-step reports preserved under tests/Verbara.Platform.LoadTests/load-test-reports-archive/."
