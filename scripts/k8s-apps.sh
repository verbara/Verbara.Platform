#!/usr/bin/env bash
# scripts/k8s-apps.sh — Install application + observability stack on Talos cluster
# Prereq: scripts/k8s-up.sh must have run (cluster + Cilium ready)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
INFRA="$REPO_ROOT/infra/k8s"
KUBECONFIG_PATH="${KUBECONFIG:-$HOME/.kube/config-talos}"
export KUBECONFIG="$KUBECONFIG_PATH"

echo "=== Verbara Platform — K8s Application Stack ==="
echo "    KUBECONFIG: $KUBECONFIG_PATH"
echo ""

kubectl get nodes --no-headers | grep -q "Ready" || { echo "ERROR: No Ready nodes. Run scripts/k8s-up.sh first."; exit 1; }

# --- 1. Storage: local-path-provisioner ---
echo "[1/10] Installing local-path-provisioner..."
if ! kubectl get storageclass local-path &>/dev/null; then
    kubectl apply -f https://raw.githubusercontent.com/rancher/local-path-provisioner/v0.0.30/deploy/local-path-storage.yaml
    kubectl -n local-path-storage rollout status deploy/local-path-provisioner --timeout=2m
    kubectl label ns local-path-storage pod-security.kubernetes.io/enforce=privileged --overwrite
fi
echo "  Done."

# --- 2. CloudNativePG operator + Postgres HA ---
echo "[2/10] Installing CloudNativePG + Postgres 3-HA..."
if ! kubectl get crd clusters.postgresql.cnpg.io &>/dev/null; then
    kubectl apply --server-side -f https://raw.githubusercontent.com/cloudnative-pg/cloudnative-pg/release-1.25/releases/cnpg-1.25.0.yaml
    kubectl -n cnpg-system rollout status deploy/cnpg-controller-manager --timeout=3m
fi
kubectl apply -f "$INFRA/manifests/cloudnativepg/cluster.yaml"
echo "  Waiting for Postgres cluster (3 instances)..."
kubectl -n r55-data wait --for=condition=Ready cluster/postgres --timeout=10m 2>/dev/null || true
kubectl apply -f "$INFRA/manifests/cloudnativepg/pooler.yaml"
echo "  Done."

# --- 3. Redis ---
echo "[3/10] Installing Redis..."
kubectl apply -f "$INFRA/manifests/redis/statefulset.yaml"
kubectl -n r55-data rollout status statefulset/redis --timeout=2m
echo "  Done."

# --- 4. Asterisk + Kamailio + RTPEngine ---
echo "[4/10] Installing Asterisk Helm chart..."
if helm status asterisk -n r55-asterisk &>/dev/null; then
    helm upgrade asterisk "$INFRA/helm/asterisk" --values "$INFRA/helm/asterisk/values.yaml"
else
    helm install asterisk "$INFRA/helm/asterisk" --values "$INFRA/helm/asterisk/values.yaml"
fi
kubectl -n r55-asterisk rollout status statefulset/asterisk --timeout=5m
echo "  Waiting for Kamailio + RTPEngine DaemonSets..."
kubectl -n r55-asterisk rollout status ds/kamailio --timeout=3m
kubectl -n r55-asterisk rollout status ds/rtpengine --timeout=3m
echo "  Done."

# --- 5. Platform.Api + Web ---
echo "[5/10] Installing Platform Helm chart..."
if helm status platform -n r55-platform &>/dev/null; then
    helm upgrade platform "$INFRA/helm/platform" --values "$INFRA/helm/platform/values.yaml"
else
    helm install platform "$INFRA/helm/platform" --values "$INFRA/helm/platform/values.yaml"
fi
kubectl -n r55-platform rollout status deploy/platform-api --timeout=5m
kubectl -n r55-platform rollout status deploy/web --timeout=2m
echo "  Done."

# --- 6. Observability ---
echo "[6/10] Installing observability stack..."
bash "$INFRA/helm/observability/install.sh"
echo "  Done."

# --- 7. Alert rules ---
echo "[7/10] Applying PrometheusRule CRD..."
kubectl apply -f "$INFRA/manifests/prometheusrules-r55.yaml"
echo "  Done."

# --- 8. NetworkPolicies ---
echo "[8/10] Applying NetworkPolicies..."
kubectl apply -f "$INFRA/manifests/network-policies.yaml"
echo "  Done."

# --- 9. ResourceQuotas ---
echo "[9/10] Applying ResourceQuotas..."
kubectl apply -f "$INFRA/manifests/resource-quotas.yaml"
echo "  Done."

# --- 10. Summary ---
echo "[10/10] Verification..."
echo ""
echo "=== Pods by namespace ==="
for ns in r55-data r55-asterisk r55-platform monitoring; do
    echo ""
    echo "--- $ns ---"
    kubectl -n "$ns" get pods 2>/dev/null || echo "  (namespace not found)"
done

echo ""
echo "=== Services ==="
echo ""
echo "Platform API:  http://api.r55.local"
echo "Web UI:        http://r55.local"
echo "Grafana:       http://grafana.r55.local (admin / r55-staging)"
echo "Prometheus:    kubectl -n monitoring port-forward svc/prometheus-kube-prometheus-prometheus 9090:9090"
echo ""
echo "=== Application stack complete ==="
