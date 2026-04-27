#!/usr/bin/env bash
# run-zap-scan.sh — OWASP ZAP active scan launcher (R5.4 internal audit Scope 1)
#
# Usage:
#   ./scripts/run-zap-scan.sh <target-url> <output-dir>
#
# Example:
#   ./scripts/run-zap-scan.sh https://staging.example.com /tmp/zap-report
#
# Prereq:
#   - Docker available + outbound network
#   - Target URL reachable from runner (staging recommended; do NOT run against prod)
#   - Authenticated context optional (use ZAP context file for cookie/JWT auth)
#
# Output:
#   <output-dir>/zap-baseline.html   (passive baseline scan, ~2 min)
#   <output-dir>/zap-active.html     (active scan, ~30-90 min depending on surface)
#   <output-dir>/zap-active.json     (machine-readable findings)
#
# Exit codes:
#   0 = no high/medium alerts
#   1 = high alert(s) found (P0/P1 candidates)
#   2 = medium alert(s) found (P2 candidates)
#   3 = scan failure (Docker / network / target unreachable)

set -euo pipefail

TARGET_URL="${1:-}"
OUTPUT_DIR="${2:-./zap-report}"

if [[ -z "$TARGET_URL" ]]; then
  echo "Usage: $0 <target-url> [output-dir]" >&2
  echo "Example: $0 https://staging.example.com /tmp/zap-report" >&2
  exit 64
fi

if [[ "$TARGET_URL" =~ prod|production ]]; then
  echo "ERROR: refusing to scan prod-looking URL '$TARGET_URL'." >&2
  echo "Active scans MUST run against staging or pre-prod only." >&2
  exit 64
fi

mkdir -p "$OUTPUT_DIR"
ABS_OUTPUT="$(cd "$OUTPUT_DIR" && pwd)"

echo "==> Pulling latest OWASP ZAP image..."
docker pull ghcr.io/zaproxy/zaproxy:stable >/dev/null

echo "==> Phase 1: passive baseline scan (~2 min)..."
docker run --rm \
  -v "$ABS_OUTPUT":/zap/wrk:rw \
  -t ghcr.io/zaproxy/zaproxy:stable \
  zap-baseline.py \
  -t "$TARGET_URL" \
  -r zap-baseline.html \
  -J zap-baseline.json \
  || BASELINE_RC=$?

BASELINE_RC=${BASELINE_RC:-0}
echo "    baseline rc=$BASELINE_RC, report: $ABS_OUTPUT/zap-baseline.html"

echo "==> Phase 2: active scan (~30-90 min)..."
echo "    NOTE: active scan sends real attack payloads. Ensure target is staging only."
docker run --rm \
  -v "$ABS_OUTPUT":/zap/wrk:rw \
  -t ghcr.io/zaproxy/zaproxy:stable \
  zap-full-scan.py \
  -t "$TARGET_URL" \
  -r zap-active.html \
  -J zap-active.json \
  || ACTIVE_RC=$?

ACTIVE_RC=${ACTIVE_RC:-0}
echo "    active rc=$ACTIVE_RC, report: $ABS_OUTPUT/zap-active.html"

echo ""
echo "==> Summary"
echo "    Baseline report : $ABS_OUTPUT/zap-baseline.html"
echo "    Active report   : $ABS_OUTPUT/zap-active.html"
echo "    Active JSON     : $ABS_OUTPUT/zap-active.json"
echo ""
echo "==> Next steps"
echo "    1. Review HTML reports for HIGH alerts."
echo "    2. Append findings to docs/security/internal-audit-YYYY-MM.md."
echo "    3. P0/P1 = block ship; P2/P3 = patch ticket."

# Translate ZAP exit codes into our severity scheme
if [[ $ACTIVE_RC -eq 1 ]] || [[ $BASELINE_RC -eq 1 ]]; then
  exit 1   # high alerts -> P0/P1
elif [[ $ACTIVE_RC -eq 2 ]] || [[ $BASELINE_RC -eq 2 ]]; then
  exit 2   # medium alerts -> P2
elif [[ $ACTIVE_RC -ge 3 ]] || [[ $BASELINE_RC -ge 3 ]]; then
  exit 3   # scan failure
else
  exit 0
fi
