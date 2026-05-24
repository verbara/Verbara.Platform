#!/usr/bin/env bash
# run-harness-talos.sh — wrapper for tests/Verbara.Platform.E2E.Harness
#
# Prepares one kubectl port-forward per Realtime pod in the Talos lab,
# hands the per-pod audit base URLs to the harness via the
# HARNESS_AUDIT_BASE_URLS env var, and runs the exactly-once scenario.
#
# Prereqs (operator setup, NOT validated by this script):
#   1. kubectl + KUBECONFIG pointed at the Talos lab admin@asterisk-platform
#      context (or set TALOS_CONTEXT below).
#   2. A test tenant exists in Platform.Api with TWO seeded users:
#        - $HARNESS_AGENT_EMAIL with Agent role + an agents-table record
#          (required by PUT /api/v1/agents/me/state)
#        - $HARNESS_PLATFORMADMIN_EMAIL with PlatformAdmin role
#          (required by GET /admin/realtime/audit)
#   3. Platform v2.4.4+ deployed (the audit endpoint shipped in PR #18).
#
# Required env vars (no sensible defaults — fail fast if unset):
#   HARNESS_API_BASE_URL       e.g. http://api.r55.local
#   HARNESS_REALTIME_HUB_URL   e.g. http://api.r55.local/hubs/platform
#   HARNESS_TENANT             e.g. acme
#   HARNESS_AGENT_EMAIL
#   HARNESS_AGENT_PASSWORD
#   HARNESS_PLATFORMADMIN_EMAIL
#   HARNESS_PLATFORMADMIN_PASSWORD
#
# Optional env vars:
#   HARNESS_NAMESPACE          k8s namespace (default: r55-platform)
#   HARNESS_REALTIME_LABEL     pod label selector (default: app.kubernetes.io/name=platform-realtime)
#   HARNESS_LOCAL_PORT_BASE    first local port for port-forward (default: 15031)
#   HARNESS_CLIENT_COUNT       SignalR clients (default: 5)
#   HARNESS_EVENT_COUNT        events to trigger (default: 10)
#   HARNESS_SETTLE_SEC         seconds to wait for fanout (default: 5)
#   HARNESS_REPORT_DIR         where reports land (default: ./harness-reports)
#   TALOS_CONTEXT              kubectl context to use (default: current-context)

set -euo pipefail

# ─── Required env vars guard ───────────────────────────────────────────────────
for var in HARNESS_API_BASE_URL HARNESS_REALTIME_HUB_URL HARNESS_TENANT \
           HARNESS_AGENT_EMAIL HARNESS_AGENT_PASSWORD \
           HARNESS_PLATFORMADMIN_EMAIL HARNESS_PLATFORMADMIN_PASSWORD; do
    if [[ -z "${!var:-}" ]]; then
        echo "ERROR: required env var $var is not set." >&2
        echo "See header comment in $0 for the full list." >&2
        exit 2
    fi
done

NAMESPACE="${HARNESS_NAMESPACE:-r55-platform}"
LABEL="${HARNESS_REALTIME_LABEL:-app.kubernetes.io/name=platform-realtime}"
LOCAL_PORT_BASE="${HARNESS_LOCAL_PORT_BASE:-15031}"
TALOS_CONTEXT_FLAG=""
if [[ -n "${TALOS_CONTEXT:-}" ]]; then
    TALOS_CONTEXT_FLAG="--context=${TALOS_CONTEXT}"
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HARNESS_PROJECT="$REPO_ROOT/tests/Verbara.Platform.E2E.Harness/Verbara.Platform.E2E.Harness.csproj"

if [[ ! -f "$HARNESS_PROJECT" ]]; then
    echo "ERROR: harness csproj not found at $HARNESS_PROJECT" >&2
    exit 2
fi

# ─── Discover Realtime pods ────────────────────────────────────────────────────
echo "[harness-wrapper] Discovering Realtime pods in namespace=$NAMESPACE selector=$LABEL ..."
mapfile -t PODS < <(
    kubectl $TALOS_CONTEXT_FLAG -n "$NAMESPACE" get pods -l "$LABEL" \
        -o jsonpath='{range .items[*]}{.metadata.name}{"\n"}{end}' \
        | grep -v '^$' || true
)

if [[ ${#PODS[@]} -eq 0 ]]; then
    echo "ERROR: no pods found in namespace=$NAMESPACE matching label=$LABEL" >&2
    exit 2
fi

echo "[harness-wrapper] Found ${#PODS[@]} pod(s):"
for p in "${PODS[@]}"; do echo "  - $p"; done

# ─── Port-forward each pod (background) ────────────────────────────────────────
declare -a PF_PIDS=()
declare -a AUDIT_URLS=()

cleanup() {
    echo "[harness-wrapper] Tearing down port-forwards..."
    for pid in "${PF_PIDS[@]:-}"; do
        if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
            kill "$pid" 2>/dev/null || true
            wait "$pid" 2>/dev/null || true
        fi
    done
}
trap cleanup EXIT INT TERM

for i in "${!PODS[@]}"; do
    POD="${PODS[$i]}"
    LOCAL_PORT=$((LOCAL_PORT_BASE + i))
    echo "[harness-wrapper] Port-forwarding $POD -> localhost:$LOCAL_PORT (target :5030)"
    kubectl $TALOS_CONTEXT_FLAG -n "$NAMESPACE" port-forward "pod/$POD" "$LOCAL_PORT:5030" \
        >/tmp/harness-pf-$POD.log 2>&1 &
    PF_PIDS+=($!)
    AUDIT_URLS+=("http://localhost:$LOCAL_PORT")
done

# ─── Wait for port-forwards to be ready ────────────────────────────────────────
echo "[harness-wrapper] Waiting up to 30s for port-forwards to become reachable..."
DEADLINE=$(( $(date +%s) + 30 ))
for url in "${AUDIT_URLS[@]}"; do
    while true; do
        if curl --silent --fail --max-time 1 "${url}/health" >/dev/null 2>&1; then
            break
        fi
        if [[ $(date +%s) -ge $DEADLINE ]]; then
            echo "ERROR: port-forward never became ready for $url" >&2
            echo "(check /tmp/harness-pf-*.log for kubectl output)" >&2
            exit 2
        fi
        sleep 0.2
    done
    echo "[harness-wrapper]   ✓ $url responsive"
done

# ─── Hand off to harness ───────────────────────────────────────────────────────
export HARNESS_AUDIT_BASE_URLS="$(IFS=, ; echo "${AUDIT_URLS[*]}")"
echo "[harness-wrapper] HARNESS_AUDIT_BASE_URLS=$HARNESS_AUDIT_BASE_URLS"
echo "[harness-wrapper] Running harness..."
echo

set +e
dotnet run --project "$HARNESS_PROJECT" -c Release
HARNESS_EXIT=$?
set -e

echo
echo "[harness-wrapper] Harness exited with code $HARNESS_EXIT."
exit $HARNESS_EXIT
