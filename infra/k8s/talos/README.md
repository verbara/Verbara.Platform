# Talos Linux Cluster — Asterisk Platform (Local)

Local Talos K8s cluster for R5.5 production-realism validation.

## Cluster Layout

| Node | IP | Role | RAM | vCPU | Disk |
|------|-----|------|-----|------|------|
| talos-cp1 | 192.168.122.10 | Control Plane | 4 GB | 2 | 20 GB |
| talos-w1 | 192.168.122.11 | Worker | 4 GB | 4 | 40 GB |
| talos-w2 | 192.168.122.12 | Worker | 4 GB | 4 | 40 GB |
| talos-w3 | 192.168.122.13 | Worker | 4 GB | 4 | 40 GB |

**Network:** libvirt `default` NAT on `192.168.122.0/24` with static DHCP reservations.
**MetalLB range:** `192.168.122.200-210` (for LoadBalancer services).
**K8s API:** `https://192.168.122.10:6443`

## Prerequisites

- `talosctl` v1.13.0+
- `kubectl` v1.35+
- `helm` v3.20+
- KVM/libvirt with `virsh`
- VM images stored in `/media/Data/Qemu-Img/`

## Quick Start

```bash
# Bootstrap from scratch (~10 min)
scripts/k8s-up.sh

# Tear down (destroys VMs + network reservations)
scripts/k8s-down.sh
```

## Manual Bootstrap

```bash
export TALOSCONFIG=$(pwd)/infra/k8s/talos/talosconfig

# Apply configs (after VMs boot from ISO)
talosctl apply-config --insecure --nodes 192.168.122.10 --file infra/k8s/talos/controlplane.yaml
talosctl apply-config --insecure --nodes 192.168.122.11 --file infra/k8s/talos/worker.yaml
talosctl apply-config --insecure --nodes 192.168.122.12 --file infra/k8s/talos/worker.yaml
talosctl apply-config --insecure --nodes 192.168.122.13 --file infra/k8s/talos/worker.yaml

# Bootstrap (once CP is installed and running)
talosctl bootstrap --nodes 192.168.122.10

# Get kubeconfig
talosctl kubeconfig --nodes 192.168.122.10 -f ~/.kube/config-talos
export KUBECONFIG=~/.kube/config-talos

# Verify
kubectl get nodes
```

## Talos Version

- Talos: v1.13.0
- Kubernetes: v1.36.0
- ISO: `talos-metal-amd64-v1.13.0.iso`
