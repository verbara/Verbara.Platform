#!/usr/bin/env bash
# Post-completion finalizer for B-LK measurement phase.
#
# Restores any git-tracked baseline reports NBomber's recursive wipe may
# have removed during the sweep, captures a final hardware snapshot, and
# runs the aggregator over the archived per-step reports.
#
# Originally shipped 2026-05-25 as part of the PR #32 validation cycle.
# Reusable for any future B-LK / C-LK / D-LK sweep finalization.
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$(dirname "$SCRIPT_DIR")"

echo "=== 1. Restore wiped v1.14.6 baseline files (no longer at risk now sweeps done) ==="
git restore tests/Verbara.Platform.LoadTests/load-test-reports/k8s-blk1-baseline/ 2>&1 || true
git restore tests/Verbara.Platform.LoadTests/load-test-reports/nbomber-log-2026043004.txt tests/Verbara.Platform.LoadTests/load-test-reports/nbomber-log-2026043005.txt 2>&1 || true
git restore tests/Verbara.Platform.LoadTests/load-test-reports/nbomber_report_2026-04-30--10-02-52.{csv,html,md} 2>&1 || true
git restore tests/Verbara.Platform.LoadTests/load-test-reports/nbomber_report_2026-05-22--12-02-10.{csv,html,md} 2>&1 || true

echo
echo "=== 2. Capture post-sweep hardware state ==="
OUT=docs/operations/r55-blk-evidence/2026-05-24-v251-baseline/hardware/post-sweep.txt
date -u +"%Y-%m-%dT%H:%M:%SZ" > "$OUT"
echo "---kubectl get hpa---" >> "$OUT"
kubectl get hpa -n r55-platform >> "$OUT" 2>&1
echo "---kubectl top nodes---" >> "$OUT"
kubectl top nodes >> "$OUT" 2>&1
echo "---kubectl top pods r55-platform---" >> "$OUT"
kubectl top pods -n r55-platform >> "$OUT" 2>&1
echo "---kubectl top pods r55-data---" >> "$OUT"
kubectl top pods -n r55-data >> "$OUT" 2>&1
echo "---kubectl get pods r55-platform (current replica count)---" >> "$OUT"
kubectl get pods -n r55-platform >> "$OUT" 2>&1

echo
echo "=== 3. Inventory archived reports ==="
ls -1 tests/Verbara.Platform.LoadTests/load-test-reports-archive/

echo
echo "=== 4. Run aggregator ==="
bash /tmp/aggregate-blk-results.sh

echo
echo "=== DONE — review sweep-summary.md ==="
echo "Next: edit docs/operations/r55-blk-evidence/2026-05-24-v251-baseline/README.md with the actual numbers + commit"
