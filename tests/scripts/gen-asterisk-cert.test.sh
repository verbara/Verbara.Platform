#!/bin/sh
# TDD spec for docker/gen-asterisk-cert.sh (Phase 3A·A).
# Pure-POSIX, host-runnable (needs openssl). No docker required.
#
#   GenCert_ShouldCreateValidSelfSignedCert_WhenDirIsEmpty
#   GenCert_ShouldLeaveCertUntouched_WhenRealCertAlreadyPresent
#   GenCert_ShouldBeIdempotent_OnSecondRun
#   GenCert_ShouldUseIpSan_WhenCnIsIpv4
#   GenCert_ShouldUseDnsSan_WhenCnIsHostname
set -eu

SCRIPT_DIR=$(cd "$(dirname "$0")/../.." && pwd)
GEN="$SCRIPT_DIR/docker/gen-asterisk-cert.sh"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

fail() { echo "FAIL: $1"; exit 1; }
pass() { echo "  ok: $1"; }

[ -f "$GEN" ] || fail "gen script not found at $GEN"
[ -x "$GEN" ] || fail "gen script not executable: $GEN"

# ── GenCert_ShouldCreateValidSelfSignedCert_WhenDirIsEmpty ────────────────────
D="$TMP/empty"; mkdir -p "$D"
"$GEN" "$D" "192.0.2.10" >/dev/null
[ -s "$D/asterisk.pem" ] || fail "pem not created"
[ -s "$D/asterisk.key" ] || fail "key not created"
openssl x509 -in "$D/asterisk.pem" -noout -subject >/dev/null 2>&1 \
    || fail "generated pem is not a valid x509 cert"
pass "GenCert_ShouldCreateValidSelfSignedCert_WhenDirIsEmpty"

# ── GenCert_ShouldUseIpSan_WhenCnIsIpv4 ──────────────────────────────────────
openssl x509 -in "$D/asterisk.pem" -noout -ext subjectAltName 2>/dev/null \
    | grep -q "IP Address:192.0.2.10" \
    || fail "expected IP SAN for an IPv4 CN"
pass "GenCert_ShouldUseIpSan_WhenCnIsIpv4"

# ── GenCert_ShouldUseDnsSan_WhenCnIsHostname ─────────────────────────────────
D2="$TMP/dns"; mkdir -p "$D2"
"$GEN" "$D2" "pbx.example.com" >/dev/null
openssl x509 -in "$D2/asterisk.pem" -noout -ext subjectAltName 2>/dev/null \
    | grep -q "DNS:pbx.example.com" \
    || fail "expected DNS SAN for a hostname CN"
pass "GenCert_ShouldUseDnsSan_WhenCnIsHostname"

# ── GenCert_ShouldLeaveCertUntouched_WhenRealCertAlreadyPresent ───────────────
D3="$TMP/mounted"; mkdir -p "$D3"
printf 'REAL-CERT' > "$D3/asterisk.pem"
printf 'REAL-KEY'  > "$D3/asterisk.key"
"$GEN" "$D3" "should-be-ignored" >/dev/null
[ "$(cat "$D3/asterisk.pem")" = "REAL-CERT" ] || fail "overwrote a present cert"
[ "$(cat "$D3/asterisk.key")" = "REAL-KEY" ]  || fail "overwrote a present key"
pass "GenCert_ShouldLeaveCertUntouched_WhenRealCertAlreadyPresent"

# ── GenCert_ShouldBeIdempotent_OnSecondRun ───────────────────────────────────
before=$(sha256sum "$D/asterisk.pem" | cut -d' ' -f1)
"$GEN" "$D" "192.0.2.10" >/dev/null
after=$(sha256sum "$D/asterisk.pem" | cut -d' ' -f1)
[ "$before" = "$after" ] || fail "second run regenerated an existing cert"
pass "GenCert_ShouldBeIdempotent_OnSecondRun"

echo "ALL PASS (gen-asterisk-cert.sh)"
