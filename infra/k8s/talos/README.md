# Talos Linux Cluster — Asterisk Platform (Local)

Local Talos K8s cluster for R5.5 production-realism validation.

## Stack

- **OS:** Talos Linux v1.13.0 (immutable, production-grade)
- **K8s:** v1.36.0
- **CNI:** Cilium 1.19.3 (eBPF, kube-proxy replacement, native routing)
- **LB:** Cilium LB-IPAM with L2 announcements (replaces MetalLB)
- **Ingress:** Cilium Gateway API v1.3.0 (replaces Traefik)
- **Observability:** Hubble (flow visibility, DNS, policy verdicts)
- **No kube-proxy** — Cilium eBPF replaces it entirely

## Cluster Layout

| Node | IP | Role | RAM | vCPU | Disk |
|------|-----|------|-----|------|------|
| talos-cp1 | 192.168.122.10 | Control Plane | 4 GB | 2 | 20 GB |
| talos-w1 | 192.168.122.11 | Worker | 4 GB | 4 | 40 GB |
| talos-w2 | 192.168.122.12 | Worker | 4 GB | 4 | 40 GB |
| talos-w3 | 192.168.122.13 | Worker | 4 GB | 4 | 40 GB |

**Network:** libvirt `default` NAT on `192.168.122.0/24` with static DHCP reservations.
**LB IP Pool:** `192.168.122.200/28` (Cilium L2 announcements for LoadBalancer services).
**K8s API:** `https://192.168.122.10:6443`
**Gateway:** `http://192.168.122.192` (Cilium Gateway API, HTTP/HTTPS)

## Prerequisites

- `talosctl` v1.13.0+
- `kubectl` v1.35+
- `helm` v3.20+
- KVM/libvirt with `virsh`
- VM images stored in `/media/Data/Qemu-Img/`

## Quick Start

```bash
# Bootstrap from scratch (~15 min)
scripts/k8s-up.sh

# Tear down (destroys VMs + disks)
scripts/k8s-down.sh --confirm
```

## Architecture Decision

Cilium was chosen over Flannel+MetalLB+Traefik based on deep analysis
(see docs/decisions/ for ADR). Cilium consolidates 5 components into 1:

| Replaced Component | Cilium Feature |
|---------------------|---------------|
| Flannel (CNI) | Cilium eBPF dataplane (53% lower p99 latency) |
| kube-proxy | eBPF kube-proxy replacement (O(1) vs O(n)) |
| MetalLB | LB-IPAM + L2 announcements |
| Traefik | Gateway API (HTTPRoute, TLSRoute) |
| Network observability | Hubble (flows, DNS, policy verdicts) |

Additional capabilities not available with Flannel stack:
- NetworkPolicy enforcement (L3/L4/L7, DNS-aware)
- Host Firewall (CiliumClusterwideNetworkPolicy for hostNetwork pods)
- Bandwidth manager (EDT + eBPF)
- Native routing (no VXLAN overhead for RTP/SIP traffic)

## Talos Machine Config

Key Talos config for Cilium (applied via `patches/cilium-cni.yaml`):

```yaml
cluster:
  network:
    cni:
      name: none    # Cilium installed post-bootstrap via Helm
  proxy:
    disabled: true  # Cilium replaces kube-proxy
```

## Useful Commands

```bash
export TALOSCONFIG=$(pwd)/infra/k8s/talos/talosconfig
export KUBECONFIG=~/.kube/config-talos

# Cluster health
kubectl get nodes -o wide
kubectl -n kube-system exec ds/cilium -- cilium status

# Hubble flow observability
kubectl -n kube-system exec ds/cilium -- hubble observe --last 20

# Hubble UI (browser)
kubectl -n kube-system port-forward svc/hubble-ui 12000:80
# Open http://localhost:12000

# Gateway status
kubectl get gateways,httproutes

# L2 announcement leases
kubectl get leases -n kube-system | grep l2
```
