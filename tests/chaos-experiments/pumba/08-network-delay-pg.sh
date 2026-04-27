#!/usr/bin/env bash
# R5.5 A.5 · Experiment 08 — 200 ms network latency on Postgres for 60 s.
#
# Validates: SLO degradation under degraded link (vs. complete partition).
# Bypasses the circuit breaker open path (latency rises without errors)
# so we can see queries-per-sec drop while pool stays healthy.
#
# Expected: SloBreachQueueIngestion P1 likely; AuditWriteLatencyP99High P1
# likely; HealthCheckUnhealthy probably NOT (heartbeats still flow).
set -euo pipefail

DURATION="${DURATION:-60s}"
TARGET="${TARGET:-re2:postgres}"
IFACE="${IFACE:-eth0}"
DELAY_MS="${DELAY_MS:-200}"

TC_IMAGE="${TC_IMAGE:-gaiadocker/iproute2}"

echo "[chaos-08] Injecting ${DELAY_MS}ms latency on Postgres ($TARGET on $IFACE) for $DURATION..."
pumba netem --tc-image "$TC_IMAGE" --duration "$DURATION" --interface "$IFACE" \
    delay --time "$DELAY_MS" "$TARGET"
echo "[chaos-08] Latency injection complete."
