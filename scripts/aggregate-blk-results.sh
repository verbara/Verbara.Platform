#!/usr/bin/env bash
# Parse all archived NBomber sweep reports (5 scenarios × rate ladder) and
# emit a single compact markdown summary table to OUT.
#
# Originally shipped 2026-05-25 alongside the PR #32 validation cycle.
# Reusable for any future scenario-sweep.sh archive.
#
# Env knobs:
#   ARCHIVE     default <repo>/tests/Verbara.Platform.LoadTests/load-test-reports-archive
#   OUT         default <repo>/docs/operations/r55-blk-evidence/<UTC-date>-aggregate/sweep-summary.md
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
ARCHIVE="${ARCHIVE:-$ROOT/tests/Verbara.Platform.LoadTests/load-test-reports-archive}"
OUT="${OUT:-$ROOT/docs/operations/r55-blk-evidence/$(date -u +%Y-%m-%d)-aggregate/sweep-summary.md}"
mkdir -p "$(dirname "$OUT")"

cat > "$OUT" << 'EOF'
# B-LK.1 sweep summary — Platform v2.5.1 on Talos lab

Parsed from `tests/Verbara.Platform.LoadTests/load-test-reports-archive/<scenario>-<var>-<value>-60s/nbomber_report_*.md`.

EOF

for scenario in jwt queues livequeue agentassist presence; do
    echo "## $scenario" >> "$OUT"
    echo "" >> "$OUT"
    echo "| Step | OK | Fail | p50 (ms) | p95 (ms) | p99 (ms) | RPS | Status codes |" >> "$OUT"
    echo "|---|---|---|---|---|---|---|---|" >> "$OUT"
    # Sort archive subdirs by numeric step (extracted from dirname suffix "-<step>-60s")
    for dir in $(for d in "$ARCHIVE"/${scenario}-*-60s; do [ -d "$d" ] && step=$(basename "$d" | sed -E 's/.*-([0-9]+)-60s$/\1/') && echo "$step $d"; done | sort -n | awk '{print $2}'); do
        step=$(basename "$dir" | sed -E "s/${scenario}-(LOADTEST_(RATE|VU))-([0-9]+)-60s/\3/")
        md=$(ls "$dir"/nbomber_report_*.md 2>/dev/null | head -1)
        [ -z "$md" ] && continue
        ok=$(grep -m1 "ok count:" "$md" | sed -E 's/.*ok count: `([0-9]+)`.*/\1/' || echo "-")
        fail=$(grep -m1 "fail count:" "$md" | sed -E 's/.*fail count: `([0-9]+)`.*/\1/' || echo "-")
        # NBomber: first "latency percentile" line is OK stats (sometimes only one if no failures);
        # if there's a 2nd (failures stats), it's after a blank line — take the FIRST always.
        percentiles=$(grep "latency percentile" "$md" | head -1)
        p50=$(echo "$percentiles" | grep -oE 'p50 = `[^`]+`' | sed -E 's/p50 = `([^`]+)`/\1/')
        p95=$(echo "$percentiles" | grep -oE 'p95 = `[^`]+`' | sed -E 's/p95 = `([^`]+)`/\1/')
        p99=$(echo "$percentiles" | grep -oE 'p99 = `[^`]+`' | sed -E 's/p99 = `([^`]+)`/\1/')
        rps=$(grep "request count" "$md" | head -1 | grep -oE 'RPS = `[^`]+`' | sed -E 's/RPS = `([^`]+)`/\1/')
        # Status codes table: get 20 lines after the header, filter to table rows, skip header + separator
        codes=$(grep -A 20 "^> status codes" "$md" | grep '^|' | tail -n +3 | sed -E 's/^\|([A-Za-z0-9_-]+) *\| *([0-9]+).*/\1=\2/' | tr '\n' ' ')
        echo "| $step | $ok | $fail | $p50 | $p95 | $p99 | $rps | $codes |" >> "$OUT"
    done
    echo "" >> "$OUT"
done

echo "Aggregated to: $OUT"
cat "$OUT"
