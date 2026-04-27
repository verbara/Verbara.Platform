#!/usr/bin/env bash
# R5.5 A.5 · Experiment 09 — Throttle Asterisk to 200 Kbps for 60 s.
#
# Validates: RTP behavior under bandwidth starvation. PCMU = 64 Kbps per
# direction × 2 (caller + callee) = 128 Kbps minimum per call. 200 Kbps
# allows ~1 active call; concurrent calls degrade to packet drop.
#
# Expected: existing calls degrade audibly; new INVITEs may stall in
# 100 Trying; PlatformApiUnavailable P0 should NOT fire (different path
# to Asterisk); BlackboxJourneyDown on asterisk:5038/8088 may flap.
set -euo pipefail

DURATION="${DURATION:-60s}"
TARGET="${TARGET:-re2:asterisk}"
IFACE="${IFACE:-eth0}"
RATE="${RATE:-200kbit}"

TC_IMAGE="${TC_IMAGE:-gaiadocker/iproute2}"

echo "[chaos-09] Throttling Asterisk ($TARGET on $IFACE) to $RATE for $DURATION..."
pumba netem --tc-image "$TC_IMAGE" --duration "$DURATION" --interface "$IFACE" \
    rate --rate "$RATE" "$TARGET"
echo "[chaos-09] Bandwidth throttle lifted."
