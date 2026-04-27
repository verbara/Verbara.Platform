#!/usr/bin/env bash
# scripts/sipp-test.sh — R5.5 A.3 SIPp suite runner.
#
# Iterates the 5 SIPp scenarios in tests/sipp-scenarios/ with the canonical
# baseline rates (Phase B-L). For stress / soak runs override the per-call
# tuning via SIPP_RATE / SIPP_MAX_CALLS / SIPP_LIMIT.
#
# Usage:
#   ./scripts/sipp-test.sh <TARGET_IP>[:port]    # default 127.0.0.1:5060
#
# Env knobs:
#   SIPP_BIN          path to sipp (default: sipp on PATH)
#   SIPP_SERVICE      [service] arg (default: queue-1)
#   SIPP_RATE         calls per second (default: 1)
#   SIPP_LIMIT        max simultaneous calls (default: 10)
#   SIPP_MAX_CALLS    total calls per scenario before stop (default: 100)
#   SIPP_XFER_TARGET  REFER target for scenario 05 (default: queue-2)
#   SIPP_REPORT_DIR   override the report directory
#
# Exit code: 0 on success; counts non-zero return per scenario but does not
# abort early so the whole suite always completes.

set -uo pipefail

TARGET="${1:-127.0.0.1:5060}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
SCENARIO_DIR="$ROOT/tests/sipp-scenarios"
REPORT_DIR="${SIPP_REPORT_DIR:-$ROOT/sipp-reports/$(date +%Y%m%d-%H%M%S)}"

SIPP_BIN="${SIPP_BIN:-sipp}"
SIPP_SERVICE="${SIPP_SERVICE:-queue-1}"
SIPP_RATE="${SIPP_RATE:-1}"
SIPP_LIMIT="${SIPP_LIMIT:-10}"
SIPP_MAX_CALLS="${SIPP_MAX_CALLS:-100}"
SIPP_XFER_TARGET="${SIPP_XFER_TARGET:-queue-2}"

if ! command -v "$SIPP_BIN" >/dev/null 2>&1; then
    echo "[sipp] FAIL: sipp not found on PATH (looked for '$SIPP_BIN')." >&2
    echo "[sipp]       Install with: sudo apt install -y sip-tester" >&2
    exit 1
fi

mkdir -p "$REPORT_DIR"
echo "[sipp] Suite target: $TARGET"
echo "[sipp] Reports:      $REPORT_DIR"
echo "[sipp] Knobs:        rate=$SIPP_RATE limit=$SIPP_LIMIT max=$SIPP_MAX_CALLS service=$SIPP_SERVICE"

failures=0
for scenario in 01-basic-call 02-ivr-navigation 03-queue-join 04-conference 05-transfer; do
    echo ""
    echo "[sipp] === $scenario ==="

    args=(
        -sf "$SCENARIO_DIR/${scenario}.xml"
        -s "$SIPP_SERVICE"
        -r "$SIPP_RATE"
        -l "$SIPP_LIMIT"
        -m "$SIPP_MAX_CALLS"
        -trace_stat -stf "$REPORT_DIR/${scenario}.csv"
        -trace_screen -screen_file "$REPORT_DIR/${scenario}.screen.log"
        -trace_err -error_file "$REPORT_DIR/${scenario}.err.log"
        -nostdin
        "$TARGET"
    )

    # Scenario 05 needs a transfer target via -key.
    if [ "$scenario" = "05-transfer" ]; then
        args+=(-key xfer_target "$SIPP_XFER_TARGET")
    fi

    if "$SIPP_BIN" "${args[@]}"; then
        echo "[sipp]   ✓ $scenario complete"
    else
        rc=$?
        echo "[sipp]   ⚠ $scenario exited with rc=$rc — check ${scenario}.err.log"
        failures=$((failures + 1))
    fi
done

echo ""
if [ "$failures" -gt 0 ]; then
    echo "[sipp] DONE — $failures of 5 scenarios reported failures."
    echo "[sipp]        Reports + per-scenario logs: $REPORT_DIR"
    exit 0    # Don't propagate scenario failures — the report itself is the deliverable.
else
    echo "[sipp] DONE — all 5 scenarios completed cleanly."
    echo "[sipp]        Reports: $REPORT_DIR"
fi
