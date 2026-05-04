#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=== Adding Helm repos ==="
helm repo add prometheus-community https://prometheus-community.github.io/helm-charts
helm repo add grafana https://grafana.github.io/helm-charts
helm repo update

echo "=== Installing kube-prometheus-stack ==="
helm upgrade --install prometheus prometheus-community/kube-prometheus-stack \
  --namespace monitoring --create-namespace \
  --values "$SCRIPT_DIR/kube-prometheus-stack-values.yaml" \
  --version 84.5.0 \
  --wait --timeout 10m

echo "=== Installing Loki ==="
helm upgrade --install loki grafana/loki \
  --namespace monitoring \
  --values "$SCRIPT_DIR/loki-values.yaml" \
  --version 6.55.0 \
  --wait --timeout 5m

echo "=== Installing blackbox-exporter ==="
helm upgrade --install blackbox prometheus-community/prometheus-blackbox-exporter \
  --namespace monitoring \
  --values "$SCRIPT_DIR/blackbox-exporter-values.yaml" \
  --version 11.9.2 \
  --wait --timeout 3m

echo "=== Verifying ==="
kubectl -n monitoring get pods
echo ""
echo "Grafana: http://grafana.r55.local (admin / r55-staging)"
echo "Prometheus: kubectl -n monitoring port-forward svc/prometheus-kube-prometheus-prometheus 9090:9090"
