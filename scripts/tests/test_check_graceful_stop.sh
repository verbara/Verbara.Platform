#!/usr/bin/env bash
# test_check_graceful_stop.sh — fake hosts for scripts/check-graceful-stop.sh, one per failure mode plus a clean run.
#
# The check guards `AOT Publish (Api)`. A check that cannot fail reads exactly like a clean stop, so every host below
# fails exactly one criterion: the case asserts that criterion is named, and that no other criterion is. The clean
# host stays the control. CHECK_GRACEFUL_STOP runs every case against another copy of the check: that is how a
# negative control shows the cases can fail. Pure bash + python3 + curl, no .NET, no Postgres.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CHECK="${CHECK_GRACEFUL_STOP:-$SCRIPT_DIR/../check-graceful-stop.sh}"
fails=0; pass=0
ok()  { pass=$((pass + 1)); }
bad() { echo "FAIL: $1"; fails=$((fails + 1)); }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

cat >"$WORK/fake_host.py" <<'PY'
import http.server, os, signal, sys, threading, time

mode, port, pid_file = sys.argv[1], int(sys.argv[2]), sys.argv[3]
with open(pid_file, "w") as f:
    f.write(str(os.getpid()))

def say(line):
    print(line, flush=True)

# The real transport logs this line at Debug only, so a fake prints it only if the check raised that category.
RAISED = os.environ.get("Logging__LogLevel__Verbara.Sdk.Pro.Cluster.Transport.PostgresClusterTransport") == "Debug"

class Health(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200 if self.path == "/health" else 404)
        self.end_headers()
    def log_message(self, *args):
        pass

if mode == "dies-at-boot":
    sys.exit(3)
if mode not in ("in-memory",) and RAISED:
    say("dbug: Verbara.Sdk.Pro.Cluster.Transport.PostgresClusterTransport[1]")
    say("      LISTEN loop started on channel 'cluster_events'")
if mode != "no-health":
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Health)
    threading.Thread(target=server.serve_forever, daemon=True).start()

def pair(count=1, stopped=True):
    for _ in range(count):
        say("info: Verbara.Sdk.Pro.Cluster.ClusterManager[1]")
        say("      [CLUSTER] Instance fake stopping")
        if stopped:
            say("info: Verbara.Sdk.Pro.Cluster.ClusterManager[2]")
            say("      [CLUSTER] Instance fake stopped")

def on_term(*_):
    if mode == "abort-134":
        pair()
        signal.signal(signal.SIGABRT, signal.SIG_DFL)
        os.kill(os.getpid(), signal.SIGABRT)
    elif mode == "no-cluster":
        pass
    elif mode == "three-stops":
        pair(3)
    elif mode == "unhandled":
        pair()
        say("Unhandled exception. System.ObjectDisposedException: The CancellationTokenSource has been disposed.")
    elif mode == "no-stopped":
        pair(stopped=False)
    else:
        pair()
    os._exit(0)

signal.signal(signal.SIGTERM, signal.SIG_IGN if mode == "hang" else on_term)
while True:
    time.sleep(0.05)
PY

free_port() {
  python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()'
}

# fake_host <mode> <port>: writes an executable taking no arguments, as the check starts its binary. In bash, not sh:
# dash drops a variable whose name holds a dot from the environment, and the check's raised log level is one.
fake_host() {
  local host="$WORK/host-$1"
  cat >"$host" <<SH
#!/bin/bash
ulimit -c 0
exec python3 "$WORK/fake_host.py" "$1" "$2" "$WORK/$1.pid"
SH
  chmod +x "$host"
  echo "$host"
}

# run_case <mode> <boot-bound> <want-rc> <regex the output must hold>...
# Each regex is a criterion this host fails; every other criterion in CRITERIA must stay silent.
CRITERIA=('exit code [0-9]+, expected' "'Unhandled exception' lines" 'cluster stopping lines' 'cluster stopped lines'
  'no proof the Postgres transport' 'did not answer within' 'did not exit within' 'before /health answered' 'already answers')
run_case() {
  local mode=$1 boot=$2 want_rc=$3 port host out rc re criterion
  shift 3
  port=$(free_port)
  host=$(fake_host "$mode" "$port")
  out=$(GRACEFUL_STOP_LOG="$WORK/$mode.log" bash "$CHECK" "$host" "http://127.0.0.1:$port" 2 "$boot" 2>&1)
  rc=$?
  if [ "$rc" -ne "$want_rc" ]; then
    bad "$mode: check exited $rc, wanted $want_rc: $out"
    return
  fi
  for re in "$@"; do
    if ! grep -Eq "$re" <<<"$out"; then
      bad "$mode: output does not name /$re/: $out"
      return
    fi
  done
  for criterion in "${CRITERIA[@]}"; do
    for re in "$@"; do [ "$re" = "$criterion" ] && continue 2; done
    if grep -E "FAIL: .*$criterion" <<<"$out" >/dev/null; then
      bad "$mode: failed for another reason too (/$criterion/): $out"
      return
    fi
  done
  ok
}

# The eight of design H3.
run_case abort-134   10 1 'exit code [0-9]+, expected' 'FAIL: exit code 134,'
run_case no-cluster  10 1 'cluster stopping lines' 'cluster stopped lines' 'FAIL: 0 cluster stopping' 'FAIL: 0 cluster stopped'
run_case three-stops 10 1 'cluster stopping lines' 'cluster stopped lines' 'FAIL: 3 cluster stopping' 'FAIL: 3 cluster stopped'
run_case unhandled   10 1 "'Unhandled exception' lines"
run_case no-stopped  10 1 'cluster stopped lines' 'FAIL: 0 cluster stopped'
run_case no-health    2 1 'did not answer within'
run_case hang        10 1 'did not exit within'
run_case clean       10 0 'PASS'

# Past H3: the stop pair is printed whatever the transport, so a host on the in-memory one must not pass.
run_case in-memory    2 1 'no proof the Postgres transport'
# A host that dies before serving, and a port something else answers, must not read as a boot timeout or as a boot.
run_case dies-at-boot 10 1 'before /health answered' 'rc=3 before'

# Spec: a hang fails AND the check kills the process.
if kill -0 "$(cat "$WORK/hang.pid")" 2>/dev/null; then
  bad "hang: the process outlived the check"
  kill -KILL "$(cat "$WORK/hang.pid")" 2>/dev/null
else
  ok
fi

# A listener that was already there: the check must refuse before starting its binary.
port=$(free_port)
python3 "$WORK/fake_host.py" clean "$port" "$WORK/squatter.pid" >/dev/null 2>&1 &
squatter=$!
for _ in $(seq 1 50); do curl -fsS -o /dev/null --max-time 1 "http://127.0.0.1:$port/health" 2>/dev/null && break; sleep 0.1; done
out=$(GRACEFUL_STOP_LOG="$WORK/squatter.log" bash "$CHECK" "$(fake_host dies-at-boot "$port")" "http://127.0.0.1:$port" 2 2 2>&1)
rc=$?
kill -KILL "$squatter" 2>/dev/null
wait "$squatter" 2>/dev/null
if [ "$rc" -eq 1 ] && grep -q 'already answers' <<<"$out"; then ok; else bad "squatter: rc=$rc: $out"; fi

# Usage errors are not verdicts.
bash "$CHECK" /nonexistent http://127.0.0.1:1 2 >/dev/null 2>&1
[ "$?" -eq 2 ] && ok || bad "a missing binary must exit 2"
bash "$CHECK" only-one-arg >/dev/null 2>&1
[ "$?" -eq 2 ] && ok || bad "a short argument list must exit 2"

echo "$pass passed, $fails failed"
[ "$fails" -eq 0 ]
