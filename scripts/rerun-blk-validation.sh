#!/usr/bin/env bash
# Rerun B-LK presence VU=1500 to validate health-contract fixes (or any
# future fix targeting the pod-restart-under-burst failure mode).
#
# Originally shipped 2026-05-25 to validate PR #32 + v2.5.2 release.
# Expected: 0 pod restarts (vs prior 4 on the unpatched v2.5.1).
#
# Env knobs:
#   PLATFORM_API_URL    default http://api.r55.local (Talos lab via Cilium GW)
#   EVIDENCE_DIR        default docs/operations/r55-blk-evidence/<UTC-date>-rerun
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
TS=$(date -u +"%Y%m%d-%H%M")
EVIDENCE="${EVIDENCE_DIR:-$ROOT/docs/operations/r55-blk-evidence/$(date -u +%Y-%m-%d)-rerun-$TS}"
mkdir -p "$EVIDENCE/hardware"

echo "=== Pre-rerun snapshot ==="
date -u +"%Y-%m-%dT%H:%M:%SZ" | tee "$EVIDENCE/T0.txt"
kubectl get pods -n r55-platform -o wide | tee -a "$EVIDENCE/T0.txt"
echo "---restartCount baseline---" | tee -a "$EVIDENCE/T0.txt"
kubectl get pods -n r55-platform -o jsonpath='{range .items[*]}{.metadata.name}{": restarts="}{.status.containerStatuses[0].restartCount}{"\n"}{end}' | tee -a "$EVIDENCE/T0.txt"

echo "=== Run presence VU=1500 only (single step, 60s) ==="
PLATFORM_API_URL="http://api.r55.local" bash "$ROOT/scripts/scenario-sweep.sh" presence 1500 2>&1 | tee "$EVIDENCE/sweep-stdout.log"

echo "=== Post-rerun snapshot ==="
date -u +"%Y-%m-%dT%H:%M:%SZ" | tee "$EVIDENCE/T1.txt"
kubectl get pods -n r55-platform -o wide | tee -a "$EVIDENCE/T1.txt"
echo "---restartCount delta---" | tee -a "$EVIDENCE/T1.txt"
kubectl get pods -n r55-platform -o jsonpath='{range .items[*]}{.metadata.name}{": restarts="}{.status.containerStatuses[0].restartCount}{"\n"}{end}' | tee -a "$EVIDENCE/T1.txt"

echo "=== Recent events (warning + killing) ==="
kubectl get events -n r55-platform --sort-by=.lastTimestamp 2>&1 | grep -iE "warning|killing|unhealthy" | tail -20 | tee "$EVIDENCE/events.txt"

echo "=== Archive: copy preserved sweep report out of NBomber's wipe-zone ==="
LATEST_ARCHIVE=$(ls -td "$ROOT/tests/Verbara.Platform.LoadTests/load-test-reports-archive/presence-"*"-1500-60s" | head -1)
if [ -d "$LATEST_ARCHIVE" ]; then
    cp -r "$LATEST_ARCHIVE" "$EVIDENCE/presence-VU1500-archive"
    echo "Archived: $LATEST_ARCHIVE"
fi

echo
echo "=== DONE — evidence in $EVIDENCE ==="
echo "Next: compare T0 restartCount vs T1. Expected delta = 0."
