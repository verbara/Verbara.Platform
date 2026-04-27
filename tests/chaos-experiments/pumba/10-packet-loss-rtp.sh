#!/usr/bin/env bash
# R5.5 A.5 · Experiment 10 — 5% packet loss on Asterisk for 60 s.
#
# Validates: SIP retransmit + RTP forward error correction behavior.
# 5% loss in PCMU is the threshold where MOS scores drop noticeably
# (3.8 → 3.4 typical) but the call remains intelligible.
#
# Expected: RTP packet-loss meter rises; SIP signaling completes via
# retransmit; ASR (Answer-Seizure Ratio) drops a few % for new calls
# whose initial INVITE is among the lost packets.
set -euo pipefail

DURATION="${DURATION:-60s}"
TARGET="${TARGET:-re2:asterisk}"
IFACE="${IFACE:-eth0}"
LOSS_PCT="${LOSS_PCT:-5}"

echo "[chaos-10] Injecting ${LOSS_PCT}% packet loss on Asterisk ($TARGET on $IFACE) for $DURATION..."
pumba netem --duration "$DURATION" --interface "$IFACE" loss --percent "$LOSS_PCT" "$TARGET"
echo "[chaos-10] Packet loss injection complete."
