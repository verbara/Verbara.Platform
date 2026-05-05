#!/usr/bin/env bash
# R5.5 A.5 · Experiment 01 — Pause Postgres for 30 s.
#
# Validates: connection-pool recovery + retry budget on the platform-api
# `Verbara.Sdk.Resilience` keyed policies. Auth queries should retry +
# eventually succeed once Postgres unfreezes; long-running queries
# (analytics, audit) should drop their pool slot via timeout.
#
# Expected: zero data loss; HTTP 5xx blip during the 30 s window;
# CircuitBreakerOpen alert may fire (P1) but NOT PgConnectionPoolHigh.
set -euo pipefail

DURATION="${DURATION:-30s}"
TARGET="${TARGET:-re2:postgres}"

echo "[chaos-01] Pausing Postgres ($TARGET) for $DURATION..."
pumba pause --duration "$DURATION" "$TARGET"
echo "[chaos-01] Pause complete; Postgres resumed."
