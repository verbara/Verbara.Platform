#!/usr/bin/env bash
# scripts/chaos-test.sh — R5.5 A.5 Pumba chaos suite runner.
#
# Iterates the 10 experiments in tests/chaos-experiments/pumba/ sequentially,
# taking pre + post Postgres + Redis snapshots so each experiment's blast
# radius can be diff'd later. Used in Phase C-L on top of an active NBomber
# + SIPp baseline run.
#
# Env knobs:
#   COMPOSE          docker-compose file (default docker/docker-compose.full.yml)
#   PG_USER          Postgres superuser (default platform; must match POSTGRES_USER)
#   PG_DB            Postgres database (default platform)
#   RECOVERY_SLEEP   Seconds to wait between experiments (default 30)
#   SNAP_DIR         Override snapshot directory
#
# Exit code: 0 — runner always exits cleanly even if individual experiments
# fail mid-flight (recovery validation is the deliverable, not return code).

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"

COMPOSE="${COMPOSE:-$ROOT/docker/docker-compose.full.yml}"
EXP_DIR="$ROOT/tests/chaos-experiments/pumba"
SNAP_DIR="${SNAP_DIR:-$ROOT/chaos-snapshots/$(date +%Y%m%d-%H%M%S)}"
PG_USER="${PG_USER:-platform}"
PG_DB="${PG_DB:-platform}"
RECOVERY_SLEEP="${RECOVERY_SLEEP:-30}"

# ---------------------------------------------------------------------------
# K8s mode (R5.5 Phase C-LK) — iterates Chaos Mesh CRD manifests under
# tests/chaos-experiments/chaos-mesh/. Selected via `--k8s` first arg.
# Independent code path: no pumba, no docker, no Postgres pg_dumpall (would
# be wrong target anyway — CNPG primary lives inside the cluster, not on the
# host's docker daemon). Snapshots are kubectl-based.
# ---------------------------------------------------------------------------
if [ "${1:-}" = "--k8s" ]; then
    OBSERVE_SEC="${OBSERVE_SEC:-90}"
    K8S_EXP_DIR="$ROOT/tests/chaos-experiments/chaos-mesh"
    REPORT_DIR="${REPORT_DIR:-$ROOT/chaos-reports/$(date +%Y%m%d-%H%M%S)}"

    if [ -z "${KUBECONFIG:-}" ]; then
        echo "[chaos-k8s] FATAL: KUBECONFIG env var required" >&2
        exit 1
    fi
    if ! command -v kubectl >/dev/null 2>&1; then
        echo "[chaos-k8s] FATAL: kubectl not on PATH" >&2
        exit 1
    fi
    if ! kubectl get crd podchaos.chaos-mesh.org >/dev/null 2>&1; then
        echo "[chaos-k8s] FATAL: Chaos Mesh CRDs not installed." >&2
        echo "[chaos-k8s]        See tests/chaos-experiments/chaos-mesh/README.md" >&2
        exit 1
    fi

    mkdir -p "$REPORT_DIR"
    echo "[chaos-k8s] reports: $REPORT_DIR"
    echo "[chaos-k8s] observation window: ${OBSERVE_SEC}s per experiment"

    kubectl get pods -A -o wide > "$REPORT_DIR/00-pre-snapshot-pods.txt" 2>&1

    for exp in "$K8S_EXP_DIR"/*.yaml; do
        [ -f "$exp" ] || continue
        NAME=$(basename "$exp" .yaml)
        echo ""
        echo "[chaos-k8s] === $NAME ==="
        LOG="$REPORT_DIR/$NAME.log"
        echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) applying" > "$LOG"

        if ! kubectl apply -f "$exp" >> "$LOG" 2>&1; then
            echo "[chaos-k8s] $NAME — apply failed"
            echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) apply-failed" >> "$LOG"
            continue
        fi

        echo "[chaos-k8s] $NAME — applied; observing ${OBSERVE_SEC}s..."
        sleep "$OBSERVE_SEC"

        {
            echo ""
            echo "=== Chaos object status ==="
            kubectl get -f "$exp" -o yaml 2>&1
            echo ""
            echo "=== Pods after observation ==="
            kubectl get pods -A -o wide 2>&1
        } >> "$LOG"

        # `kubectl delete` issues the deletion request; Chaos Mesh's controller
        # owns the actual teardown via finalizers. On Cilium kube-proxy-
        # replacement clusters, NetworkChaos teardown fails (the controller
        # cannot reverse the iptables injection that was never applied), so
        # the resource sits in `Terminating` forever with its finalizer intact.
        # That blocks the next `kubectl apply -f` of the same name in the
        # next sweep. Background-delete (--wait=false) then poll up to 10s
        # for the resource to disappear; if it's still stuck, force-clear the
        # finalizer so the next sweep starts clean. Recorded as C-LK v2.5.2
        # finding #9 (docs/operations/chaos-test-report-k8s-local.md).
        kubectl delete -f "$exp" --ignore-not-found --wait=false >> "$LOG" 2>&1
        for _i in 1 2 3 4 5; do
            if ! kubectl get -f "$exp" >/dev/null 2>&1; then
                break
            fi
            sleep 2
        done
        if kubectl get -f "$exp" >/dev/null 2>&1; then
            STUCK_KIND=$(kubectl get -f "$exp" -o jsonpath='{.kind}' 2>/dev/null || true)
            STUCK_NAME=$(kubectl get -f "$exp" -o jsonpath='{.metadata.name}' 2>/dev/null || true)
            STUCK_NS=$(kubectl get -f "$exp" -o jsonpath='{.metadata.namespace}' 2>/dev/null || true)
            echo "[chaos-k8s] $NAME — finalizer stuck on ${STUCK_KIND}/${STUCK_NAME} (ns=${STUCK_NS:-default}); force-clearing"
            echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) finalizer-force-clear ${STUCK_KIND}/${STUCK_NAME}" >> "$LOG"
            kubectl patch -f "$exp" --type=merge -p '{"metadata":{"finalizers":null}}' >> "$LOG" 2>&1 || true
        fi
        echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) cleaned" >> "$LOG"
        echo "[chaos-k8s] $NAME — cleaned"
    done

    kubectl get pods -A -o wide > "$REPORT_DIR/zz-post-snapshot-pods.txt" 2>&1

    echo ""
    echo "[chaos-k8s] suite complete. Reports: $REPORT_DIR"
    echo "[chaos-k8s] Diff pre/post pod state:"
    echo "[chaos-k8s]   diff $REPORT_DIR/00-pre-snapshot-pods.txt $REPORT_DIR/zz-post-snapshot-pods.txt"
    exit 0
fi

# ---------------------------------------------------------------------------
# Default mode (R5.5 Phase C-L) — Pumba against docker-compose stack.
# ---------------------------------------------------------------------------

if ! command -v pumba >/dev/null 2>&1; then
    echo "[chaos] FAIL: pumba not found on PATH." >&2
    echo "[chaos]       Install with:" >&2
    echo "[chaos]         curl -sL https://github.com/alexei-led/pumba/releases/download/0.10.0/pumba_linux_amd64 -o /tmp/pumba" >&2
    echo "[chaos]         chmod +x /tmp/pumba && sudo mv /tmp/pumba /usr/local/bin/pumba" >&2
    exit 1
fi

mkdir -p "$SNAP_DIR"
echo "[chaos] Suite snapshot dir: $SNAP_DIR"
echo "[chaos] Compose:            $COMPOSE"
echo "[chaos] Recovery sleep:     ${RECOVERY_SLEEP}s"

snapshot() {
    local stage="$1"
    local pg_container redis_container
    pg_container=$(docker ps --filter name=postgres --format '{{.Names}}' | head -1)
    if [ -n "$pg_container" ]; then
        docker exec "$pg_container" pg_dumpall -U "$PG_USER" \
            > "$SNAP_DIR/$stage-pg.sql" 2>/dev/null || \
            echo "[chaos]   note: pg snapshot for $stage skipped (server unreachable?)"
    fi
    redis_container=$(docker ps --filter name=redis --format '{{.Names}}' | head -1)
    if [ -n "$redis_container" ]; then
        docker exec "$redis_container" redis-cli SAVE >/dev/null 2>&1 || true
        docker cp "$redis_container":/data/dump.rdb \
            "$SNAP_DIR/$stage-redis.rdb" 2>/dev/null || \
            echo "[chaos]   note: redis snapshot for $stage skipped"
    fi
}

count=0
for exp in "$EXP_DIR"/*.sh; do
    [ -f "$exp" ] || continue
    name=$(basename "$exp" .sh)
    count=$((count + 1))
    echo ""
    echo "[chaos] === [$count] $name ==="
    snapshot "${name}-pre"
    if "$exp"; then
        echo "[chaos]   ✓ $name script returned 0"
    else
        rc=$?
        echo "[chaos]   ⚠ $name returned $rc (some experiments are disruptive — check report)"
    fi
    echo "[chaos]   Sleeping ${RECOVERY_SLEEP}s for recovery..."
    sleep "$RECOVERY_SLEEP"
    snapshot "${name}-post"
done

echo ""
echo "[chaos] DONE — $count experiments executed."
echo "[chaos] Snapshots: $SNAP_DIR"
