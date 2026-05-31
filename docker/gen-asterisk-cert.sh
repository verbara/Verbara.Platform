#!/bin/sh
# Generate a self-signed TLS cert for Asterisk's HTTPS/WSS listener (port 8089)
# into <cert-dir> when one is not already present. WebRTC softphones (Phase 3A)
# register over wss://host:8089, which requires the HTTP TLS listener to have a
# usable certificate.
#
# Idempotent: a non-empty cert+key pair already present (e.g. a real mounted
# cert, or a prior run) is left untouched. Exits non-zero when <cert-dir> is not
# writable, so the caller can fall back to another directory.
#
#   usage: gen-asterisk-cert.sh <cert-dir> [common-name]
set -eu

CERT_DIR="${1:?usage: gen-asterisk-cert.sh <cert-dir> [common-name]}"
CN="${2:-localhost}"
CERT="$CERT_DIR/asterisk.pem"
KEY="$CERT_DIR/asterisk.key"

if [ -s "$CERT" ] && [ -s "$KEY" ]; then
    echo "[gen-cert] cert already present at $CERT — leaving untouched"
    exit 0
fi

mkdir -p "$CERT_DIR" 2>/dev/null || true

# A WSS cert's SAN type must match how the browser dialed in: IP:<addr> for a
# bare IPv4 CN, DNS:<name> otherwise. A mismatch makes browsers reject the cert.
if echo "$CN" | grep -Eq '^[0-9]{1,3}(\.[0-9]{1,3}){3}$'; then
    SAN="IP:$CN"
else
    SAN="DNS:$CN"
fi

openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
    -keyout "$KEY" -out "$CERT" \
    -subj "/CN=$CN" -addext "subjectAltName=$SAN"
chmod 600 "$KEY" 2>/dev/null || true
echo "[gen-cert] generated self-signed cert CN=$CN ($SAN) at $CERT"
