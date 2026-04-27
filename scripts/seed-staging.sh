#!/usr/bin/env bash
# scripts/seed-staging.sh — R5.5 multi-tenant staging seed (Phase 0L · A.1)
#
# Creates 3 tenants (small/medium/large) with realistic agent + queue counts
# matching the v1-provisional capacity tiers in
# `docs/operations/capacity-planning.md`. Output drives all R5.5 measurement
# runs (NBomber baseline + SIPp + Pumba chaos + 24h soak + cloud replication).
#
# Idempotent: safe to re-run. Caches the platform admin access token in
# `docker/.staging-admin-token` (gitignored) so subsequent runs skip the setup
# wizard. Existing tenants are detected via 409 Conflict and skipped.
#
# Configuration (env overrides):
#   PLATFORM_API_URL  — default http://localhost:5000
#   ADMIN_TOKEN       — pre-existing platform admin Bearer (skips setup)
#   ADMIN_EMAIL       — default platform-admin@r55-staging.local (only used on
#                       first call; ignored if setup already ran)
#   ADMIN_PASSWORD    — default PlatformAdmin2026! (same caveat)
#
# Output: prints `ADMIN_TOKEN=…` as the LAST line so callers can capture via
#         `tail -1 | sed 's/.*ADMIN_TOKEN=//'`.
#
# Requires: bash 4+, curl, jq.

set -euo pipefail

PLATFORM_API_URL="${PLATFORM_API_URL:-http://localhost:5000}"
ADMIN_EMAIL="${ADMIN_EMAIL:-platform-admin@r55-staging.local}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-PlatformAdmin2026!}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TOKEN_FILE="$REPO_ROOT/docker/.staging-admin-token"

log() { printf '[seed] %s\n' "$*" >&2; }

require() {
    command -v "$1" >/dev/null 2>&1 || { log "FATAL: missing dependency: $1"; exit 1; }
}
require curl
require jq

# ---------------------------------------------------------------------------
# Step 1 — obtain platform admin access token
# ---------------------------------------------------------------------------
ADMIN_TOKEN="${ADMIN_TOKEN:-}"

if [ -z "$ADMIN_TOKEN" ] && [ -f "$TOKEN_FILE" ]; then
    ADMIN_TOKEN=$(cat "$TOKEN_FILE")
    # Validate cached token
    code=$(curl -s -o /dev/null -w "%{http_code}" \
        -H "Authorization: Bearer $ADMIN_TOKEN" \
        "$PLATFORM_API_URL/api/v1/management/tenants" || echo "000")
    if [ "$code" = "200" ] || [ "$code" = "401" ] && [ "$code" = "401" ]; then
        log "Cached token expired, re-acquiring."
        ADMIN_TOKEN=""
    elif [ "$code" = "200" ]; then
        log "Cached token valid (PLATFORM_API_URL=$PLATFORM_API_URL)."
    else
        log "Cached token check returned HTTP $code — re-acquiring."
        ADMIN_TOKEN=""
    fi
fi

if [ -z "$ADMIN_TOKEN" ]; then
    log "Attempting platform setup wizard..."
    setup_resp=$(curl -s -w "\n%{http_code}" -X POST "$PLATFORM_API_URL/api/v1/setup" \
        -H "Content-Type: application/json" \
        -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\",\"displayName\":\"R55 Platform Admin\",\"platformName\":\"R5.5 Staging\"}" || true)
    setup_code=$(echo "$setup_resp" | tail -n1)
    setup_body=$(echo "$setup_resp" | head -n-1)

    if [ "$setup_code" = "201" ]; then
        ADMIN_TOKEN=$(echo "$setup_body" | jq -r '.accessToken')
        log "Platform initialized. Token cached at $TOKEN_FILE."
    elif [ "$setup_code" = "409" ]; then
        log "Platform already initialized — falling back to login."
        login_resp=$(curl -s -X POST "$PLATFORM_API_URL/api/v1/auth/login" \
            -H "Content-Type: application/json" \
            -H "X-Tenant-Id: platform" \
            -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\"}" || true)
        ADMIN_TOKEN=$(echo "$login_resp" | jq -r '.accessToken // empty')
        if [ -z "$ADMIN_TOKEN" ] || [ "$ADMIN_TOKEN" = "null" ]; then
            log "FATAL: setup returned 409 and login failed. Set ADMIN_TOKEN env var manually."
            log "       Login response: $login_resp"
            exit 2
        fi
        log "Logged in as $ADMIN_EMAIL."
    else
        log "FATAL: setup returned HTTP $setup_code"
        log "       Body: $setup_body"
        exit 2
    fi

    printf '%s' "$ADMIN_TOKEN" > "$TOKEN_FILE"
    chmod 600 "$TOKEN_FILE"
fi

# ---------------------------------------------------------------------------
# Step 2 — seed each tenant profile
# ---------------------------------------------------------------------------
seed_tenant() {
    local profile="$1"
    local profile_file="$SCRIPT_DIR/seed-data/${profile}-tenant.json"

    [ -f "$profile_file" ] || { log "FATAL: profile not found: $profile_file"; return 2; }

    local tenant_id name agents queues ext_base
    tenant_id=$(jq -r '.tenantId'       "$profile_file")
    name=$(     jq -r '.name'           "$profile_file")
    agents=$(   jq -r '.agents'         "$profile_file")
    queues=$(   jq -r '.queues'         "$profile_file")
    ext_base=$( jq -r '.extensionBase'  "$profile_file")

    log ""
    log "==> $tenant_id (${agents} agents, ${queues} queues, ext base ${ext_base})"

    # 2a. Create tenant
    local create_body
    create_body=$(jq -n \
        --arg tid  "$tenant_id" \
        --arg name "$name" \
        --argjson meta "$(jq '.metadata' "$profile_file")" \
        '{tenantId:$tid, name:$name, type:"Customer", parentTenantId:"platform", metadata:$meta}')
    local resp_code
    resp_code=$(curl -s -o /dev/null -w "%{http_code}" -X POST \
        "$PLATFORM_API_URL/api/v1/management/tenants" \
        -H "Authorization: Bearer $ADMIN_TOKEN" \
        -H "Content-Type: application/json" \
        -d "$create_body")
    case "$resp_code" in
        201|204) log "    tenant created" ;;
        409)     log "    tenant already exists — skipping create" ;;
        *)       log "    WARN: tenant create returned HTTP $resp_code" ;;
    esac

    # 2b. Create users + agents (paired: each agent links to a created user).
    log "    seeding ${agents} users + agents..."
    local i ext user_resp user_id agent_resp
    for i in $(seq 1 "$agents"); do
        ext=$((ext_base + i))

        user_resp=$(curl -s -X POST "$PLATFORM_API_URL/api/v1/admin/users" \
            -H "Authorization: Bearer $ADMIN_TOKEN" \
            -H "X-Tenant-Id: $tenant_id" \
            -H "Content-Type: application/json" \
            -d "{\"email\":\"agent${i}@${tenant_id}.local\",\"displayName\":\"Agent ${i}\",\"role\":\"Agent\",\"password\":\"Agent2026!\"}" || true)
        # POST /admin/users returns { id, email, displayName, ... } — never `userId` at top-level.
        user_id=$(echo "$user_resp" | jq -r '.id // empty')
        if [ -z "$user_id" ] || [ "$user_id" = "null" ]; then
            # User likely exists — fall back to paginated list lookup.
            # GET /admin/users returns { items: [...], totalCount, page, ... }.
            user_id=$(curl -s -G "$PLATFORM_API_URL/api/v1/admin/users" \
                --data-urlencode "email=agent${i}@${tenant_id}.local" \
                --data-urlencode "pageSize=1" \
                -H "Authorization: Bearer $ADMIN_TOKEN" \
                -H "X-Tenant-Id: $tenant_id" \
                | jq -r --arg email "agent${i}@${tenant_id}.local" \
                    '(.items // []) | map(select(.email==$email)) | (first // {}).id // empty')
        fi
        if [ -z "$user_id" ] || [ "$user_id" = "null" ]; then
            log "    WARN: could not resolve user id for agent${i} (last create resp: ${user_resp:0:200})"
            continue
        fi

        agent_resp=$(curl -s -X POST "$PLATFORM_API_URL/api/v1/admin/agents" \
            -H "Authorization: Bearer $ADMIN_TOKEN" \
            -H "X-Tenant-Id: $tenant_id" \
            -H "Content-Type: application/json" \
            -d "{\"userId\":\"${user_id}\",\"displayName\":\"Agent ${i}\",\"extension\":\"${ext}\",\"sipPassword\":\"sip-${tenant_id}-${i}\"}" || true)
    done
    log "    ${agents} users + agents done"

    # 2c. Create queues.
    log "    seeding ${queues} queues..."
    for i in $(seq 1 "$queues"); do
        curl -s -o /dev/null -X POST "$PLATFORM_API_URL/api/v1/admin/queues" \
            -H "Authorization: Bearer $ADMIN_TOKEN" \
            -H "X-Tenant-Id: $tenant_id" \
            -H "Content-Type: application/json" \
            -d "{\"name\":\"queue-${i}\",\"maxWaiting\":60}" || true
    done
    log "    ${queues} queues done"

    log "    ✓ $tenant_id seeded"
}

for profile in small medium large; do
    seed_tenant "$profile"
done

# ---------------------------------------------------------------------------
# Step 3 — verification + emit token for downstream consumers
# ---------------------------------------------------------------------------
log ""
log "Verifying seed via /api/v1/management/tenants..."
tenant_count=$(curl -s "$PLATFORM_API_URL/api/v1/management/tenants" \
    -H "Authorization: Bearer $ADMIN_TOKEN" | jq 'length // 0')
log "  tenants visible: $tenant_count"

log ""
log "Done. Use the token cached at $TOKEN_FILE for subsequent runs."
echo "ADMIN_TOKEN=$ADMIN_TOKEN"
