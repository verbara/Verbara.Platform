#!/usr/bin/env bash
# verbara-smoke-released.sh — post-release FUNCTIONAL smoke check.
#
# docker/verbara-verify-image.sh proves PROVENANCE only ("this image was
# signed by us"). It never boots the image or exercises it. This script is
# the next gate: bring up the demo-compose substrate pinned to a release's
# cosign-verified digests, wait on binary readiness, and run ONE real
# end-to-end journey against Postgres. Walking-skeleton scope
# (openspec/changes/released-image-smoke) — ONE journey, not full coverage:
#
#   POST /api/v1/setup        creates the host + first Customer tenant AND
#                              their admin users, persisted to Postgres.
#   POST /api/v1/auth/login   re-reads the just-created user from Postgres
#                              and mints a JWT.
#
# This is the cheapest journey in docker/demo/demo-reset.sh that proves the
# released api image actually boots, talks to Postgres, and serves a
# request end-to-end — not just that its signature verifies.
#
# Substrate: docker/demo/docker-compose.demo.yml (postgres + redis +
# platform-api only — Asterisk/pstn-emulator/realtime/web/nginx-gateway/
# prometheus/grafana are never started; AsteriskAmiHealthCheck reports
# Healthy when Asterisk:Ami:Hostname is unset — "digital-only deployment",
# see src/Verbara.Platform.Api/Health/AsteriskAmiHealthCheck.cs), re-pinned
# to the released api digest via docker/smoke/docker-compose.smoke-
# override.yml.
#
# Usage:
#   ./verbara-smoke-released.sh [--tag vX.Y.Z] [--local]
#
#   --tag vX.Y.Z   Resolve digests for this release tag instead of the
#                  latest GitHub release.
#   --local        Skip the ghcr.io pull entirely and build+use the demo
#                  compose's own local images (`docker compose build`).
#                  For dev use when ghcr.io pull is infeasible (no
#                  registry auth, size/bandwidth) — does NOT verify the
#                  actually-released artifact; see README note.
#
# Requirements: docker (with compose plugin), curl, python3 (stdlib json
# only — no extra deps, mirrors demo-reset.sh's existing use of python3).
# `gh` is used only to resolve the latest release when neither --tag nor
# --local is given (falls back to the public authorized-digests.json if
# `gh` is unavailable/unauthenticated).
#
# Exit codes:
#   0  smoke journey passed
#   1  journey failed (released images boot but the functional check failed)
#   2  environment/setup error (missing tool, digest resolution failed, etc.)
#   3  readiness timed out (diagnostic-only outer bound — see WAIT_* below)
#
# Always tears the stack down on exit (trap), whether it passed or failed.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEMO_DIR="$SCRIPT_DIR/demo"
COMPOSE_BASE="$DEMO_DIR/docker-compose.demo.yml"
COMPOSE_OVERRIDE="$SCRIPT_DIR/smoke/docker-compose.smoke-override.yml"
ENV_FILE="$DEMO_DIR/.env.demo"
PROJECT_NAME="verbara-smoke"

TAG=""
LOCAL_MODE=false

while [ $# -gt 0 ]; do
  case "$1" in
    --tag)
      TAG="${2:-}"
      if [ -z "$TAG" ]; then
        echo "Error: --tag requires a value (e.g. --tag v2.16.0)" >&2
        exit 2
      fi
      shift 2
      ;;
    --local)
      LOCAL_MODE=true
      shift
      ;;
    -h|--help)
      sed -n '2,45p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      echo "Error: unknown argument '$1'" >&2
      exit 2
      ;;
  esac
done

log()  { printf '[%s] %s\n' "$(date -u +%H:%M:%S)" "$1"; }
fail() { printf '\n=== SMOKE FAILED: %s ===\n' "$1" >&2; }

COMPOSE=(docker compose -p "$PROJECT_NAME" -f "$COMPOSE_BASE" -f "$COMPOSE_OVERRIDE" --env-file "$ENV_FILE")

teardown() {
  local exit_code=$?
  log "Tearing down smoke stack (exit code so far: $exit_code)..."
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  exit "$exit_code"
}
trap teardown EXIT

# ─── 1. Resolve the platform-api image reference ────────────────────────────

if [ "$LOCAL_MODE" = true ]; then
  log "Local mode (--local): building demo compose's own platform-api image."
  # DEVIATION (documented in the implementation manifest): this branch does
  # NOT pull or verify a released ghcr.io digest. It exists so contributors
  # can exercise this script's journey/readiness logic without registry
  # access. It does not substitute for running the default (non-`--local`)
  # path against a real release before trusting a tag.
  ( cd "$DEMO_DIR" && docker compose -p "$PROJECT_NAME" -f "$COMPOSE_BASE" --env-file "$ENV_FILE" build platform-api )
  PLATFORM_API_IMAGE="${PROJECT_NAME}-platform-api"
  # Retag the just-built image under a stable local ref so the override file's
  # `image:` (no build:) resolves without a registry pull.
  docker tag "$(docker compose -p "$PROJECT_NAME" -f "$COMPOSE_BASE" --env-file "$ENV_FILE" images -q platform-api)" \
    "$PLATFORM_API_IMAGE" 2>/dev/null || true
else
  for cmd in curl python3; do
    if ! command -v "$cmd" >/dev/null 2>&1; then
      echo "Error: $cmd not found on PATH." >&2
      exit 2
    fi
  done

  if [ -z "$TAG" ]; then
    log "No --tag given; resolving the latest Verbara.Platform release..."
    if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then
      TAG="$(gh api repos/verbara/Verbara.Platform/releases/latest --jq '.tag_name' 2>/dev/null || true)"
    fi
    if [ -z "$TAG" ]; then
      log "gh unavailable/unauthenticated; falling back to authorized-digests.json."
      LATEST_JSON="$(curl -fsS https://verbara.io/data/authorized-digests.json 2>/dev/null || true)"
      if [ -n "$LATEST_JSON" ]; then
        TAG="$(printf '%s' "$LATEST_JSON" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['current'][-1].get('tag',''))" 2>/dev/null || true)"
      fi
    fi
    if [ -z "$TAG" ]; then
      fail "could not resolve latest release tag (no gh auth, no reachable authorized-digests.json). Pass --tag explicitly or use --local."
      exit 2
    fi
  fi
  log "Resolving released digest for tag ${TAG}..."

  DIGEST=""
  if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then
    RELEASE_JSON="$(gh api "repos/verbara/Verbara.Platform/releases/tags/${TAG}" 2>/dev/null || true)"
    if [ -n "$RELEASE_JSON" ]; then
      DIGEST="$(printf '%s' "$RELEASE_JSON" | python3 -c "
import sys, json, re
body = json.load(sys.stdin).get('body', '')
m = re.search(r'\*\*api\*\* \(Native AOT\)\s*\|\s*\`(sha256:[0-9a-f]{64})\`', body)
print(m.group(1) if m else '')
" 2>/dev/null || true)"
    fi
  fi
  if [ -z "$DIGEST" ]; then
    log "Falling back to docker buildx imagetools inspect for the tag's digest..."
    DIGEST="$(docker buildx imagetools inspect "ghcr.io/verbara/platform/api:${TAG}" --format '{{.Manifest.Digest}}' 2>/dev/null || true)"
  fi
  if [ -z "$DIGEST" ]; then
    fail "could not resolve manifest-list digest for ghcr.io/verbara/platform/api:${TAG}"
    exit 2
  fi

  PLATFORM_API_IMAGE="ghcr.io/verbara/platform/api@${DIGEST}"
  log "Resolved: ${PLATFORM_API_IMAGE}"

  # Layer B pre-flight: same signature-only check as verbara-verify-image.sh
  # (same repo-committed key, same --insecure-ignore-tlog posture — see
  # release.yml's own "Verify cosign signature" step for the precedent) so a
  # smoke run never proceeds against an unsigned/tampered image. NOT invoked
  # via verbara-verify-image.sh itself: that script (a) only accepts a
  # `repo:tag` ref and re-derives the digest itself (ours is already
  # digest-form — passing it through mangles the ref into `repo@tag@digest`),
  # and (b) fetches its key from https://verbara.io/.well-known/cosign.pub,
  # which 404s as of this writing (unrelated pre-existing site gap, out of
  # scope here — see manifest deviation). We verify directly against the
  # repo-committed docker/cosign.pub instead, which is byte-identical to
  # .github/cosign.pub (CI digest-reconciliation enforces that parity) and
  # is the same key material verbara-verify-image.sh is trying to use.
  if command -v cosign >/dev/null 2>&1; then
    log "Verifying cosign signature (Layer B) before pulling..."
    if ! cosign verify --key "$SCRIPT_DIR/cosign.pub" --insecure-ignore-tlog "$PLATFORM_API_IMAGE" >/dev/null 2>&1; then
      fail "cosign signature verification failed for ${PLATFORM_API_IMAGE}"
      exit 2
    fi
  else
    log "WARNING: cosign not found on PATH — skipping signature pre-flight (functional smoke only)."
  fi

  log "Pulling ${PLATFORM_API_IMAGE}..."
  if ! docker pull "$PLATFORM_API_IMAGE"; then
    fail "docker pull failed for ${PLATFORM_API_IMAGE} (auth/network/size?). Retry with --local for a dev-only local build instead."
    exit 2
  fi
fi

export PLATFORM_API_IMAGE
log "platform-api image pinned to: ${PLATFORM_API_IMAGE}"

# ─── 2. Bring up the minimal substrate (postgres, redis, platform-api) ──────

log "Starting smoke stack (postgres, redis, platform-api)..."
"${COMPOSE[@]}" up -d postgres redis platform-api

# ─── 3. Binary readiness — poll /health/ready, NEVER a sleep as pass/fail ───
# (ADR-0004 determinism fences: the fixed WAIT_TIMEOUT_SEC bound below is a
# diagnostic-only outer ceiling to stop CI from hanging forever on a truly
# wedged container; it never substitutes for the health signal itself. Each
# iteration polls the binary /health/ready result.)

API_BASE="http://localhost:5000"
WAIT_TIMEOUT_SEC="${WAIT_TIMEOUT_SEC:-180}"
WAIT_POLL_INTERVAL_SEC=2

log "Waiting on ${API_BASE}/health/ready (binary poll, outer diagnostic bound ${WAIT_TIMEOUT_SEC}s)..."
elapsed=0
ready=false
while [ "$elapsed" -lt "$WAIT_TIMEOUT_SEC" ]; do
  if curl -fsS -o /dev/null "${API_BASE}/health/ready"; then
    ready=true
    break
  fi
  sleep "$WAIT_POLL_INTERVAL_SEC"
  elapsed=$((elapsed + WAIT_POLL_INTERVAL_SEC))
done

if [ "$ready" != true ]; then
  fail "platform-api never reported /health/ready within the ${WAIT_TIMEOUT_SEC}s diagnostic bound"
  echo "--- docker compose ps ---" >&2
  "${COMPOSE[@]}" ps >&2 || true
  echo "--- platform-api logs (last 200 lines) ---" >&2
  "${COMPOSE[@]}" logs --tail=200 platform-api >&2 || true
  exit 3
fi
log "platform-api is ready."

# ─── 3b. Community-boot readiness CONTRACT (Verbara.Sdk.Pro/ADR-0017) ────────
# The demo substrate boots UNLICENSED (no Pro license mounted), so the Pro
# dialer stack's ready-tagged `dialer-engine` check is license-blocked. Post
# the 2.13.0-pro pin bump it settles Degraded (HTTP 200), not Unhealthy (503),
# which is why /health/ready above returned 2xx at all. A bare 200 is NOT
# enough: assert the exact consumed wire shape emitted by
# HealthReportJsonWriter — `checks.dialer-engine.status == "Degraded"` and a
# `description` starting with the stable `dialer license blocked:` prefix. We
# pin the PREFIX only; the reason suffix (NotLicensed / Revoked / Expired /
# GraceExhausted) is not part of the contract (design D2). Reuses the existing
# python3 stdlib json parse — no new tool dependency.
log "Asserting community-boot readiness contract on /health/ready (dialer-engine Degraded)..."
READY_BODY="$(curl -fsS "${API_BASE}/health/ready" 2>/dev/null || true)"
if [ -z "$READY_BODY" ]; then
  fail "could not fetch /health/ready body for the dialer-engine Degraded assertion"
  exit 1
fi
DIALER_CONTRACT="$(printf '%s' "$READY_BODY" | python3 -c "
import sys, json
d = json.load(sys.stdin)
e = d.get('checks', {}).get('dialer-engine')
if e is None:
    print('MISSING'); sys.exit(0)
status = e.get('status', '')
desc = e.get('description', '') or ''
if status != 'Degraded':
    print('STATUS:' + status); sys.exit(0)
if not desc.startswith('dialer license blocked:'):
    print('PREFIX:' + desc); sys.exit(0)
print('OK')
" 2>/dev/null || printf 'PARSE_ERROR')"

case "$DIALER_CONTRACT" in
  OK)
    log "Community-boot readiness contract OK (dialer-engine Degraded, 'dialer license blocked:' prefix present)." ;;
  MISSING)
    fail "/health/ready has no checks.dialer-engine entry — the Pro dialer readiness check is not registered (expected on a Postgres-configured community boot)"
    echo "Response body: ${READY_BODY}" >&2
    exit 1 ;;
  STATUS:*)
    fail "checks.dialer-engine.status is '${DIALER_CONTRACT#STATUS:}' — expected 'Degraded' (the 2.13.0-pro license-block-as-Degraded contract, Verbara.Sdk.Pro/ADR-0017)"
    echo "Response body: ${READY_BODY}" >&2
    exit 1 ;;
  PREFIX:*)
    fail "checks.dialer-engine.description '${DIALER_CONTRACT#PREFIX:}' does not start with 'dialer license blocked:' (cross-repo contract drift)"
    echo "Response body: ${READY_BODY}" >&2
    exit 1 ;;
  *)
    fail "could not parse checks.dialer-engine from /health/ready body"
    echo "Response body: ${READY_BODY}" >&2
    exit 1 ;;
esac

# ─── 4. The ONE end-to-end journey: setup -> login (touches Postgres) ───────

log "Running journey: POST /api/v1/setup ..."
SETUP_RESPONSE="$(curl -sS -w '\n%{http_code}' -X POST "${API_BASE}/api/v1/setup" \
  -H "Content-Type: application/json" \
  -d '{
    "email": "smoke-platform-admin@verbara.invalid",
    "password": "SmokePlatform2026!",
    "displayName": "Smoke Platform Admin",
    "platformName": "Verbara Smoke",
    "customerTenantId": "smoke",
    "customerName": "Smoke Tenant",
    "customerAdminEmail": "smoke-admin@verbara.invalid",
    "customerAdminPassword": "SmokeAdmin2026!",
    "customerAdminDisplayName": "Smoke Admin"
  }' 2>/dev/null || true)"
SETUP_HTTP_CODE="$(printf '%s' "$SETUP_RESPONSE" | tail -n1)"
SETUP_BODY="$(printf '%s' "$SETUP_RESPONSE" | sed '$d')"

if [ "$SETUP_HTTP_CODE" != "201" ]; then
  fail "POST /api/v1/setup returned HTTP ${SETUP_HTTP_CODE} (expected 201)"
  echo "Response body: ${SETUP_BODY}" >&2
  exit 1
fi
log "Setup OK (host tenant + customer tenant 'smoke' created in Postgres)."

log "Running journey: POST /api/v1/auth/login ..."
LOGIN_RESPONSE="$(curl -sS -w '\n%{http_code}' -X POST "${API_BASE}/api/v1/auth/login" \
  -H "Content-Type: application/json" \
  -d '{
    "tenantId": "smoke",
    "email": "smoke-admin@verbara.invalid",
    "password": "SmokeAdmin2026!"
  }' 2>/dev/null || true)"
LOGIN_HTTP_CODE="$(printf '%s' "$LOGIN_RESPONSE" | tail -n1)"
LOGIN_BODY="$(printf '%s' "$LOGIN_RESPONSE" | sed '$d')"

if [ "$LOGIN_HTTP_CODE" != "200" ]; then
  fail "POST /api/v1/auth/login returned HTTP ${LOGIN_HTTP_CODE} (expected 200)"
  echo "Response body: ${LOGIN_BODY}" >&2
  exit 1
fi

ACCESS_TOKEN="$(printf '%s' "$LOGIN_BODY" | python3 -c "import sys,json; print(json.load(sys.stdin).get('accessToken',''))" 2>/dev/null || true)"
if [ -z "$ACCESS_TOKEN" ]; then
  fail "login succeeded (HTTP 200) but response had no accessToken — contract drift?"
  echo "Response body: ${LOGIN_BODY}" >&2
  exit 1
fi

log "Login OK (JWT issued for the just-created Postgres-persisted user)."
log "=== SMOKE PASSED: released image boots, serves traffic, and round-trips Postgres ==="
exit 0
