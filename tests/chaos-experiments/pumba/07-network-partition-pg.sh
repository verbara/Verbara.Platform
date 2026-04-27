#!/usr/bin/env bash
# R5.5 A.5 · Experiment 07 — Network partition Postgres for 60 s.
#
# Validates: ResiliencePolicy circuit breaker behavior on a 100% packet-loss
# scenario (vs. pause which freezes the process — different recovery path).
# All Platform.Api -> Postgres traffic times out at the TCP layer.
#
# Expected: CircuitBreakerOpen P1 fires within 60 s; PgConnectionPoolHigh
# P2 may fire as the pool refuses new acquisitions; auth login latency
# spikes; recovery once partition lifts < 30 s.
set -euo pipefail

DURATION="${DURATION:-60s}"
TARGET="${TARGET:-re2:postgres}"
IFACE="${IFACE:-eth0}"

echo "[chaos-07] Partitioning Postgres network ($TARGET on $IFACE) for $DURATION..."
pumba netem --duration "$DURATION" --interface "$IFACE" loss --percent 100 "$TARGET"
echo "[chaos-07] Partition lifted."
