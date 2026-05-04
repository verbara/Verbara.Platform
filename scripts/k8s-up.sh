#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TALOS_DIR="$REPO_ROOT/infra/k8s/talos"
MANIFESTS_DIR="$REPO_ROOT/infra/k8s/manifests"
TALOSCONFIG="$TALOS_DIR/talosconfig"
KUBECONFIG_PATH="$HOME/.kube/config-talos"
IMG_DIR="/media/Data/Qemu-Img"
ISO="$IMG_DIR/talos-metal-amd64-v1.13.0.iso"
VIRSH="virsh --connect qemu:///system"

CP_IP="192.168.122.10"
WORKER_IPS=("192.168.122.11" "192.168.122.12" "192.168.122.13")
ALL_IPS=("$CP_IP" "${WORKER_IPS[@]}")

CP_MAC="52:54:00:a0:00:10"
WORKER_MACS=("52:54:00:a0:00:11" "52:54:00:a0:00:12" "52:54:00:a0:00:13")

VM_NAMES=("talos-cp1" "talos-w1" "talos-w2" "talos-w3")
VM_MACS=("$CP_MAC" "${WORKER_MACS[@]}")
VM_RAM=(4096 4096 4096 4096)
VM_VCPUS=(2 4 4 4)
VM_DISK_SIZE=("20G" "40G" "40G" "40G")

CILIUM_VERSION="1.19.3"
GATEWAY_API_VERSION="v1.3.0"

echo "=== Asterisk Platform — Talos K8s Cluster Bootstrap ==="
echo "    Talos v1.13.0 · K8s 1.36.0 · Cilium $CILIUM_VERSION (eBPF)"
echo ""

# --- Step 1: Ensure libvirt network ---
echo "[1/9] Checking libvirt network..."
if ! $VIRSH net-info default &>/dev/null; then
    echo "  ERROR: libvirt 'default' network not found. Create it first."
    exit 1
fi
if ! $VIRSH net-info default 2>/dev/null | grep -q "Active:.*yes"; then
    echo "  Starting default network..."
    $VIRSH net-start default
fi

for i in "${!VM_NAMES[@]}"; do
    if ! $VIRSH net-dumpxml default 2>/dev/null | grep -q "${VM_MACS[$i]}"; then
        hostname="${VM_NAMES[$i]}"
        ip="${ALL_IPS[$i]}"
        mac="${VM_MACS[$i]}"
        echo "  Adding DHCP reservation: $hostname → $ip ($mac)"
        $VIRSH net-update default add ip-dhcp-host \
            "<host mac=\"$mac\" name=\"$hostname\" ip=\"$ip\"/>" --live --config
    fi
done
echo "  Network ready."

# --- Step 2: Check ISO ---
echo "[2/9] Checking Talos ISO..."
if [[ ! -f "$ISO" ]]; then
    echo "  Downloading Talos v1.13.0 metal ISO..."
    wget -q --show-progress -O "$ISO" \
        "https://github.com/siderolabs/talos/releases/download/v1.13.0/metal-amd64.iso"
fi
echo "  ISO: $ISO"

# --- Step 3: Create VMs ---
echo "[3/9] Creating VMs..."
for i in "${!VM_NAMES[@]}"; do
    name="${VM_NAMES[$i]}"
    disk="$IMG_DIR/${name}.qcow2"

    if $VIRSH dominfo "$name" &>/dev/null; then
        echo "  $name already exists, starting if needed..."
        $VIRSH start "$name" 2>/dev/null || true
        continue
    fi

    if [[ ! -f "$disk" ]]; then
        echo "  Creating disk: $disk (${VM_DISK_SIZE[$i]})"
        qemu-img create -f qcow2 "$disk" "${VM_DISK_SIZE[$i]}"
    fi

    echo "  Creating VM: $name (${VM_RAM[$i]} MB, ${VM_VCPUS[$i]} vCPU)"
    virt-install --connect qemu:///system \
        --name "$name" \
        --ram "${VM_RAM[$i]}" \
        --vcpus "${VM_VCPUS[$i]}" \
        --cpu host-passthrough \
        --os-variant generic \
        --disk "path=$disk,format=qcow2,bus=virtio" \
        --cdrom "$ISO" \
        --network "network=default,mac=${VM_MACS[$i]},model=virtio" \
        --graphics none \
        --console pty,target_type=serial \
        --boot hd,cdrom \
        --noautoconsole \
        --noreboot
done

# --- Step 4: Wait for DHCP leases ---
echo "[4/9] Waiting for all VMs to get DHCP leases..."
for ip in "${ALL_IPS[@]}"; do
    until $VIRSH net-dhcp-leases default 2>/dev/null | grep -q "$ip"; do
        sleep 3
    done
    echo "  $ip — lease acquired"
done

# --- Step 5: Apply Talos configs (cni:none + proxy:disabled for Cilium) ---
echo "[5/9] Applying Talos machine configs..."
export TALOSCONFIG

talosctl apply-config --insecure --nodes "$CP_IP" --file "$TALOS_DIR/controlplane.yaml"
echo "  Control plane config applied."

for ip in "${WORKER_IPS[@]}"; do
    talosctl apply-config --insecure --nodes "$ip" --file "$TALOS_DIR/worker.yaml"
    echo "  Worker $ip config applied."
done

# --- Step 6: Bootstrap etcd ---
echo "[6/9] Waiting for control plane to be ready for bootstrap..."
until talosctl --nodes "$CP_IP" get machinestatus &>/dev/null; do
    sleep 5
done
echo "  Control plane responding. Bootstrapping etcd..."
talosctl bootstrap --nodes "$CP_IP"

# --- Step 7: Get kubeconfig ---
echo "[7/9] Getting kubeconfig..."
until talosctl kubeconfig --nodes "$CP_IP" --force -f "$KUBECONFIG_PATH" 2>/dev/null; do
    sleep 5
done
export KUBECONFIG="$KUBECONFIG_PATH"

echo "  Waiting for K8s API server..."
until kubectl get nodes &>/dev/null; do
    sleep 5
done

# --- Step 8: Install Cilium (CNI + kube-proxy replacement + LB-IPAM + Gateway API + Hubble) ---
echo "[8/9] Installing Cilium $CILIUM_VERSION..."
helm repo add cilium https://helm.cilium.io/ --force-update &>/dev/null

echo "  Installing Gateway API CRDs ${GATEWAY_API_VERSION}..."
for crd in gatewayclasses gateways httproutes referencegrants grpcroutes; do
    kubectl apply -f "https://raw.githubusercontent.com/kubernetes-sigs/gateway-api/${GATEWAY_API_VERSION}/config/crd/standard/gateway.networking.k8s.io_${crd}.yaml" &>/dev/null
done

echo "  Installing Cilium Helm chart..."
helm install cilium cilium/cilium --version "$CILIUM_VERSION" \
    --namespace kube-system \
    --values "$MANIFESTS_DIR/cilium-values.yaml" \
    --wait --timeout 5m

echo "  Applying Cilium LB IP Pool + L2 announcements..."
kubectl apply -f "$MANIFESTS_DIR/cilium-lb-pool.yaml"

echo "  Applying Gateway resources..."
kubectl apply -f "$MANIFESTS_DIR/cilium-gateway.yaml"

# --- Step 9: Wait for all nodes Ready ---
echo "[9/9] Waiting for all 4 nodes to be Ready..."
until kubectl get nodes --no-headers 2>/dev/null | grep -v "NotReady" | grep -c "Ready" | grep -q "4"; do
    sleep 5
done

echo ""
echo "=== Cluster Ready ==="
kubectl get nodes -o wide
echo ""
echo "Cilium status:"
kubectl -n kube-system exec ds/cilium -- cilium status --brief 2>/dev/null || true
echo ""
echo "Gateway:"
kubectl get gateways 2>/dev/null || true
echo ""
echo "export TALOSCONFIG=$TALOSCONFIG"
echo "export KUBECONFIG=$KUBECONFIG_PATH"
