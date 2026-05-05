#!/usr/bin/env bash
# scripts/profiling/aot-probe-publish.sh — AHH Phase 0 AOT validation gate.
#
# Publishes tests/Verbara.Platform.Api.Aot.Probe with PublishAot=true and
# asserts ZERO IL2xxx / IL3xxx trim/AOT warnings. If any warning is emitted,
# the candidate Argon2id library (Isopoh.Cryptography.Argon2) is rejected
# and Phase 4 must pivot to the libsodium P/Invoke fallback documented in
# docs/research/2026-04-XX-auth-hotpath-baseline.md.
#
# Output:
# - Native binary at:
#     tests/Verbara.Platform.Api.Aot.Probe/bin/Release/net10.0/<rid>/publish/
# - Full publish log echoed to stdout (capture for the research doc).
#
# Exit codes:
#   0 — clean publish (no IL2xxx/IL3xxx warnings, exit code 0 from runtime).
#   1 — publish emitted at least one IL trim/AOT warning.
#   2 — publish itself failed (compilation error, missing tool, etc.).
#   3 — published binary fails to run (Argon2.Verify roundtrip broken).
#
# Repro:
#   ./scripts/profiling/aot-probe-publish.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$(dirname "$SCRIPT_DIR")")"
PROBE_DIR="$ROOT/tests/Verbara.Platform.Api.Aot.Probe"
LOG="/tmp/aot-probe-publish.log"

cd "$ROOT"

if ! command -v dotnet >/dev/null 2>&1; then
    echo "[aot-probe] FAIL: dotnet CLI not found in PATH." >&2
    exit 2
fi

echo "[aot-probe] Publishing $PROBE_DIR with PublishAot=true ..."
echo "[aot-probe] Full log: $LOG"

set +e
dotnet publish "$PROBE_DIR/Verbara.Platform.Api.Aot.Probe.csproj" \
    -c Release \
    -p:PublishAot=true \
    --nologo \
    --verbosity minimal \
    > "$LOG" 2>&1
PUBLISH_RC=$?
set -e

if [ "$PUBLISH_RC" -ne 0 ]; then
    echo "[aot-probe] FAIL: publish returned exit code $PUBLISH_RC." >&2
    echo "[aot-probe]       Tail of log:" >&2
    tail -40 "$LOG" >&2
    exit 2
fi

# Trim/AOT warnings have IDs IL2xxx and IL3xxx. We grep the publish log for
# the canonical "warning IL" pattern so a single hit fails the gate.
if grep -E 'warning IL[0-9]+' "$LOG" >/dev/null 2>&1; then
    echo "[aot-probe] FAIL: trim/AOT warnings detected. Phase 4 must pivot to libsodium." >&2
    echo "[aot-probe] Warnings:" >&2
    grep -E 'warning IL[0-9]+' "$LOG" >&2
    exit 1
fi

# Locate the published binary (RID-specific subdirectory).
PUBLISH_DIR=$(find "$PROBE_DIR/bin/Release/net10.0" -mindepth 2 -maxdepth 2 -type d -name 'publish' | head -n1)
if [ -z "$PUBLISH_DIR" ] || [ ! -d "$PUBLISH_DIR" ]; then
    echo "[aot-probe] FAIL: publish output directory not found under $PROBE_DIR/bin/Release/net10.0/." >&2
    exit 2
fi

BIN="$PUBLISH_DIR/Verbara.Platform.Api.Aot.Probe"
if [ ! -x "$BIN" ]; then
    echo "[aot-probe] FAIL: native binary missing or not executable at $BIN." >&2
    exit 2
fi

echo "[aot-probe] Running native binary: $BIN"
if ! "$BIN"; then
    echo "[aot-probe] FAIL: native binary exited non-zero (Argon2 verify roundtrip broken under AOT)." >&2
    exit 3
fi

NATIVE_SIZE=$(stat -c%s "$BIN" 2>/dev/null || stat -f%z "$BIN")
echo "[aot-probe] PASS: zero IL warnings, native runtime check ok (binary size: $NATIVE_SIZE bytes)."
