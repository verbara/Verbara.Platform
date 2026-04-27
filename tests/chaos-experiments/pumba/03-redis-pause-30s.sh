#!/usr/bin/env bash
# R5.5 A.5 · Experiment 03 — Pause Redis for 30 s.
#
# Validates: SignalR backplane fallback + Identity.Redis JTI cache graceful
# degradation. Some envs may not run Redis; the experiment exits cleanly
# in that case (informational only).
set -euo pipefail

DURATION="${DURATION:-30s}"
TARGET="${TARGET:-re2:redis}"

if ! docker ps --format '{{.Names}}' | grep -qi redis; then
    echo "[chaos-03] No Redis container detected — skipping (Redis is optional)."
    exit 0
fi

echo "[chaos-03] Pausing Redis ($TARGET) for $DURATION..."
pumba pause --duration "$DURATION" "$TARGET"
echo "[chaos-03] Resumed."
