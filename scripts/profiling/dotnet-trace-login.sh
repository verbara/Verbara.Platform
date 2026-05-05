#!/usr/bin/env bash
# scripts/profiling/dotnet-trace-login.sh — AHH Phase 0 flame graph capture.
#
# Wraps `dotnet-trace collect` against the running Platform.Api process while
# a curl loop drives /api/v1/auth/login at the documented R5.5 sustainable
# rate (50 req/s by default). Produces a Speedscope-format trace plus the
# raw .nettrace; both are referenced by the Phase 0 research doc to attribute
# wall time per top-level method (BCrypt verify, JWT issuance, Postgres
# round-trips, etc.).
#
# Pre-conditions:
# - docker-compose.full.yml stack up (./scripts/load-test.sh staging once).
# - docker/.staging-admin-token populated (./scripts/seed-staging.sh).
# - dotnet-trace tool installed:
#     dotnet tool install --global dotnet-trace
#
# Output:
# - /tmp/auth-login-<timestamp>.nettrace
# - /tmp/auth-login-<timestamp>.speedscope.json
# - Echoed instructions for opening the trace in https://speedscope.app
#
# Env knobs:
#   PLATFORM_API_URL     default http://localhost:5000
#   TRACE_DURATION_SEC   default 30
#   TRACE_LOGIN_RATE     default 50  (req/s; matches measured knee)
#   TRACE_PROCESS_NAME   default Verbara.Platform.Api  (dotnet-trace --name)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$(dirname "$SCRIPT_DIR")")"

PLATFORM_API_URL="${PLATFORM_API_URL:-http://localhost:5000}"
DURATION="${TRACE_DURATION_SEC:-30}"
RATE="${TRACE_LOGIN_RATE:-50}"
PROCESS_NAME="${TRACE_PROCESS_NAME:-Verbara.Platform.Api}"

if ! command -v dotnet-trace >/dev/null 2>&1; then
    echo "[dotnet-trace-login] FAIL: dotnet-trace not in PATH." >&2
    echo "[dotnet-trace-login]       Install with: dotnet tool install --global dotnet-trace" >&2
    exit 2
fi

if ! curl -fsS -o /dev/null "$PLATFORM_API_URL/health"; then
    echo "[dotnet-trace-login] FAIL: $PLATFORM_API_URL/health unreachable." >&2
    exit 3
fi

if [ ! -f "$ROOT/docker/.staging-admin-token" ]; then
    echo "[dotnet-trace-login] FAIL: docker/.staging-admin-token missing — run ./scripts/seed-staging.sh first." >&2
    exit 4
fi

# Resolve the loadtest tenant/user credentials the same way jwt-sweep.sh does.
LOGIN_EMAIL="${TRACE_LOGIN_EMAIL:-loadtest@loadtest.local}"
LOGIN_PASSWORD="${TRACE_LOGIN_PASSWORD:-loadtest}"
LOGIN_TENANT="${TRACE_LOGIN_TENANT:-loadtest}"

TS=$(date +%Y-%m-%d--%H-%M-%S)
NETTRACE="/tmp/auth-login-${TS}.nettrace"
SPEEDSCOPE="/tmp/auth-login-${TS}.speedscope.json"

# Find the dotnet PID matching the process name.
PID=$(dotnet-trace ps 2>/dev/null | awk -v name="$PROCESS_NAME" '$0 ~ name {print $1; exit}')
if [ -z "$PID" ]; then
    echo "[dotnet-trace-login] FAIL: process matching '$PROCESS_NAME' not found." >&2
    echo "[dotnet-trace-login]       Available: " >&2
    dotnet-trace ps >&2 || true
    exit 5
fi

echo "[dotnet-trace-login] Target PID: $PID ($PROCESS_NAME)"
echo "[dotnet-trace-login] Duration: ${DURATION}s @ ${RATE} req/s"
echo "[dotnet-trace-login] nettrace: $NETTRACE"

# Collect in background, drive the load loop, then wait for collect to finish.
dotnet-trace collect \
    --process-id "$PID" \
    --providers Microsoft-DotNETCore-SampleProfiler,Microsoft-AspNetCore-Server-Kestrel,Verbara.Platform.Auth.JwtKeyRotation \
    --duration "00:00:${DURATION}" \
    --output "$NETTRACE" \
    --format Speedscope &
TRACE_PID=$!

# Drive the load. Use a simple curl loop; rate is approximate (per-second
# wall sleep), close enough for a 30s flame-graph window.
sleep 1  # let dotnet-trace spin up

end=$((SECONDS + DURATION - 1))
sent=0
while [ "$SECONDS" -lt "$end" ]; do
    for _ in $(seq 1 "$RATE"); do
        curl -fsS -o /dev/null -X POST "$PLATFORM_API_URL/api/v1/auth/login" \
            -H 'Content-Type: application/json' \
            -H "X-Tenant-Id: $LOGIN_TENANT" \
            -d "{\"email\":\"$LOGIN_EMAIL\",\"password\":\"$LOGIN_PASSWORD\"}" &
        sent=$((sent + 1))
    done
    wait
    sleep 1
done

# wait for dotnet-trace collect to finish writing
wait "$TRACE_PID" || true

echo "[dotnet-trace-login] Sent $sent login requests."
echo "[dotnet-trace-login] Speedscope JSON: ${NETTRACE%.nettrace}.speedscope.json"
echo "[dotnet-trace-login] Open https://www.speedscope.app/ and drag-drop the .speedscope.json"
echo "[dotnet-trace-login] Capture top-10 methods by self-time into the Phase 0 research doc."
