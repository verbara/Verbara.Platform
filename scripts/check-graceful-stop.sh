#!/usr/bin/env bash
# check-graceful-stop.sh — starts a published API binary, sends it SIGTERM, and fails unless the stop was clean.
#
#   check-graceful-stop.sh <binary> <base-url> <stop-bound-seconds> [boot-bound-seconds]
#
# The caller sets the host's environment (ASPNETCORE_URLS must match <base-url>). Pass needs ALL of:
#   - the process exits within the stop bound, with exit code == 0. Only 0 is asserted, never a red value: the same
#     defect reads 134 or 139 depending on what runs the process;
#   - no `Unhandled exception` in the log;
#   - exactly one `[CLUSTER] Instance ... stopping` line and one `stopped` line: a host that never activated the
#     cluster cannot pass, and a stop that runs more than once fails;
#   - the Postgres transport was the one in use. The stop lines come from ClusterManager whatever the transport, and
#     AddVerbaraCluster defaults to the in-memory one, so they alone do not prove this. The only trace of the transport
#     on a clean run is its Debug line, so this script raises that one category for the child and waits for the line.
#
# The verdict reads the log after the process exited, so nothing is still being written. Every criterion that fails
# prints its own FAIL line. Exit: 0 pass, 1 fail, 2 usage. Fake hosts: scripts/tests/test_check_graceful_stop.sh.
set -uo pipefail

if [ "$#" -lt 3 ] || [ "$#" -gt 4 ]; then
  echo "usage: $0 <binary> <base-url> <stop-bound-seconds> [boot-bound-seconds]" >&2
  exit 2
fi
BIN=$1
URL=${2%/}
STOP_BOUND=$3
BOOT_BOUND=${4:-120}
if [ ! -x "$BIN" ]; then
  echo "check-graceful-stop: FAIL: not an executable file: $BIN" >&2
  exit 2
fi

TRANSPORT_CATEGORY=Verbara.Sdk.Pro.Cluster.Transport.PostgresClusterTransport
TRANSPORT_LINE="LISTEN loop started on channel 'cluster_events'"
LOG_FILE=${GRACEFUL_STOP_LOG:-$(mktemp -t graceful-stop.XXXXXX)}
: >"$LOG_FILE"

say() { echo "check-graceful-stop: $*"; }
fails=0
fail() { say "FAIL: $*"; fails=$((fails + 1)); }
health() { curl -fsS -o /dev/null --max-time 2 "$URL/health" 2>/dev/null; }

verdict() {
  if [ "$fails" -ne 0 ]; then
    say "log tail ($LOG_FILE):"
    tail -n 40 "$LOG_FILE"
    say "FAILED: $fails criteria"
    exit 1
  fi
  say "PASS"
  exit 0
}

# A stale listener would answer /health for a host that never came up.
if health; then
  fail "$URL/health already answers before the host started"
  verdict
fi

env "Logging__LogLevel__${TRANSPORT_CATEGORY}=Debug" "$BIN" >"$LOG_FILE" 2>&1 &
pid=$!
trap 'kill -KILL "$pid" 2>/dev/null' EXIT

healthy=0
deadline=$((SECONDS + BOOT_BOUND))
while [ "$SECONDS" -lt "$deadline" ]; do
  if health; then healthy=1; break; fi
  kill -0 "$pid" 2>/dev/null || break
  sleep 0.2
done
if [ "$healthy" -ne 1 ]; then
  if kill -0 "$pid" 2>/dev/null; then
    kill -KILL "$pid" 2>/dev/null
    wait "$pid" 2>/dev/null
    fail "/health did not answer within ${BOOT_BOUND}s; process killed"
  else
    wait "$pid"
    fail "the host exited with rc=$? before /health answered"
  fi
  verdict
fi

# The listener is a task of its own, so its line can follow /health. A stop before it would be a false failure.
while [ "$SECONDS" -lt "$deadline" ]; do
  grep -qF "$TRANSPORT_LINE" "$LOG_FILE" && break
  kill -0 "$pid" 2>/dev/null || break
  sleep 0.2
done

t0=$(date +%s%N)
kill -TERM "$pid" 2>/dev/null
limit=$((t0 + STOP_BOUND * 1000000000))
while kill -0 "$pid" 2>/dev/null; do
  if [ "$(date +%s%N)" -ge "$limit" ]; then
    kill -KILL "$pid" 2>/dev/null
    wait "$pid" 2>/dev/null
    fail "the host did not exit within ${STOP_BOUND}s of SIGTERM; process killed"
    verdict
  fi
  sleep 0.05
done
wait "$pid"
rc=$?
stop_ms=$((($(date +%s%N) - t0) / 1000000))
trap - EXIT

stopping=$(grep -Ec '\[CLUSTER\] Instance [^ ]+ stopping' "$LOG_FILE")
stopped=$(grep -Ec '\[CLUSTER\] Instance [^ ]+ stopped' "$LOG_FILE")
unhandled=$(grep -c 'Unhandled exception' "$LOG_FILE")
transport=$(grep -cF "$TRANSPORT_LINE" "$LOG_FILE")
say "rc=$rc stop_ms=$stop_ms stopping=$stopping stopped=$stopped unhandled=$unhandled transport_lines=$transport"

[ "$rc" -eq 0 ] || fail "exit code $rc, expected 0"
[ "$unhandled" -eq 0 ] || fail "$unhandled 'Unhandled exception' lines, expected 0"
[ "$stopping" -eq 1 ] || fail "$stopping cluster stopping lines, expected 1"
[ "$stopped" -eq 1 ] || fail "$stopped cluster stopped lines, expected 1"
[ "$transport" -ge 1 ] || fail "no proof the Postgres transport ran: no line \"$TRANSPORT_LINE\""
verdict
