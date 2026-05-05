#!/usr/bin/env bash
# scripts/profiling/run-benchmarks.sh — AHH Phase 0 evidence runner.
#
# Runs BenchmarkDotNet against tests/Verbara.Platform.Benchmarks (opt-in,
# NOT in the slnx). Captures the BCrypt12 vs Argon2id-OWASP comparison +
# JWT-RSA sign cost + composite end-to-end estimate per AHH Phase 0.
#
# Output:
# - BenchmarkDotNet.Artifacts/results/* (markdown + csv + html per bench)
# - Per-run terminal log echoed to stdout.
#
# Env knobs:
#   BENCH_FILTER  default '*AuthHotPathBench*' (BenchmarkDotNet --filter glob)
#   BENCH_JOB     default '' (e.g. 'short' for fast smoke; '' = full run)
#
# Repro:
#   ./scripts/profiling/run-benchmarks.sh
#   BENCH_FILTER='*Argon2id*' ./scripts/profiling/run-benchmarks.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$(dirname "$SCRIPT_DIR")")"

BENCH_FILTER="${BENCH_FILTER:-*AuthHotPathBench*}"
BENCH_JOB="${BENCH_JOB:-}"

cd "$ROOT"

# Ensure dotnet-counters is available (informational, the bench itself only needs `dotnet`).
if ! command -v dotnet >/dev/null 2>&1; then
    echo "[run-benchmarks] FAIL: dotnet CLI not found in PATH." >&2
    exit 2
fi

EXTRA_ARGS=()
if [ -n "$BENCH_JOB" ]; then
    EXTRA_ARGS+=("--job" "$BENCH_JOB")
fi

echo "[run-benchmarks] Filter: $BENCH_FILTER"
[ -n "$BENCH_JOB" ] && echo "[run-benchmarks] Job: $BENCH_JOB"
echo "[run-benchmarks] Building Release..."

dotnet build tests/Verbara.Platform.Benchmarks/Verbara.Platform.Benchmarks.csproj \
    -c Release \
    --nologo \
    --verbosity minimal

echo "[run-benchmarks] Running..."

dotnet run \
    --project tests/Verbara.Platform.Benchmarks/Verbara.Platform.Benchmarks.csproj \
    -c Release \
    --no-build \
    -- \
    --filter "$BENCH_FILTER" \
    "${EXTRA_ARGS[@]}"

echo "[run-benchmarks] Done. Reports under: BenchmarkDotNet.Artifacts/results/"
