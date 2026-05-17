# Chaos Mesh experiments — R5.5 Phase C-LK

K8s-native chaos engineering experiments via [Chaos Mesh](https://chaos-mesh.org/) for the R5.5 production-validation suite (`Phase C-LK · Stress + chaos K8s local`).

## Inventory

| # | File | Type | Target | Duration | Risk |
|---|---|---|---|---|---|
| 01 | `01-pg-replica-pod-kill.yaml` | PodChaos | Postgres replica | one-shot | low |
| 02 | `02-platform-api-pod-kill.yaml` | PodChaos | Platform.Api pod | one-shot | low |
| 03 | `03-redis-pod-kill.yaml` | PodChaos | Redis pod | one-shot | medium |
| 04 | `04-asterisk-pod-kill.yaml` | PodChaos | Asterisk pod | one-shot | medium |
| 05 | `05-kamailio-pod-kill.yaml` | PodChaos | Kamailio pod | one-shot | medium |
| 06 | `06-platform-api-network-delay.yaml` | NetworkChaos | Platform.Api egress | 60s | low |
| 07 | `07-pg-network-partition.yaml` | NetworkChaos | Postgres primary ↔ Platform.Api | 60s | high |
| 08 | `08-platform-api-cpu-stress.yaml` | StressChaos | Platform.Api CPU | 90s | medium |
| 09 | `09-platform-api-memory-stress.yaml` | StressChaos | Platform.Api memory | 60s | medium |
| 10 | `10-cnpg-primary-failover.yaml` | PodChaos | CNPG primary | one-shot | high |

## Prerequisites

```bash
helm repo add chaos-mesh https://charts.chaos-mesh.org
helm install chaos-mesh chaos-mesh/chaos-mesh \
    --namespace=chaos-mesh --create-namespace --version 2.7.0 \
    --set chaosDaemon.runtime=containerd \
    --set chaosDaemon.socketPath=/run/containerd/containerd.sock
kubectl label ns chaos-mesh \
    pod-security.kubernetes.io/enforce=privileged --overwrite
kubectl -n chaos-mesh rollout status daemonset/chaos-daemon --timeout=3m
```

## Run individually

```bash
export KUBECONFIG=$HOME/.kube/config-talos
kubectl apply -f tests/chaos-experiments/chaos-mesh/01-pg-replica-pod-kill.yaml
# observe
kubectl -n r55-data get pods -w
# cleanup when done
kubectl delete -f tests/chaos-experiments/chaos-mesh/01-pg-replica-pod-kill.yaml --ignore-not-found
```

## Run full suite

```bash
KUBECONFIG=$HOME/.kube/config-talos ./scripts/chaos-test.sh --k8s
```

Each experiment runs for 90 s observation window then is deleted before next.
Skip-on-error semantics: if an apply fails, the suite continues with the next
experiment.

## Cleanup all

```bash
kubectl delete podchaos,networkchaos,iochaos,stresschaos,httpchaos --all -A
```

## Observation

- **Chaos dashboard:** `kubectl -n chaos-mesh port-forward svc/chaos-dashboard 2333:2333` → http://localhost:2333
- **Grafana K8s dashboards:** http://grafana.r55.local (admin / r55-staging) → "kubernetes / Compute Resources / Pod"
- **App-level metrics:** Platform.Api `/metrics` scraped by Prometheus; query via Grafana

## Notes

- Experiments 07 + 10 are the most invasive; run last and in isolation.
- Experiment 08 CPU stress will likely trigger livenessProbe restarts on the
  lab cluster (single-replica anti-affinity preferred-not-required keeps both
  platform-api pods on talos-w3 — see B-LK.3 finding).
- HPA scale-up (#08) requires `metrics-server` which is NOT installed on this
  lab cluster; HPA stays at 2 replicas regardless of CPU. Not blocking; cloud
  phase (Phase 0C+) will install metrics-server.
