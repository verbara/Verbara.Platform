#!/usr/bin/env bash
set -euo pipefail

VIRSH="virsh --connect qemu:///system"
IMG_DIR="/media/Data/Qemu-Img"
VM_NAMES=("talos-cp1" "talos-w1" "talos-w2" "talos-w3")
KUBECONFIG_PATH="$HOME/.kube/config-talos"

echo "=== Asterisk Platform — Talos K8s Cluster Teardown ==="

if [[ "${1:-}" != "--confirm" ]]; then
    echo ""
    echo "This will DESTROY the Talos cluster:"
    echo "  - Stop and undefine all 4 VMs"
    echo "  - Delete VM disk images"
    echo "  - Remove kubeconfig"
    echo ""
    echo "Usage: $0 --confirm"
    exit 1
fi

echo ""

for name in "${VM_NAMES[@]}"; do
    if $VIRSH dominfo "$name" &>/dev/null; then
        echo "Destroying $name..."
        $VIRSH destroy "$name" 2>/dev/null || true
        $VIRSH undefine "$name" --remove-all-storage 2>/dev/null || true
    fi
    disk="$IMG_DIR/${name}.qcow2"
    if [[ -f "$disk" ]]; then
        echo "  Removing disk: $disk"
        rm -f "$disk"
    fi
done

if [[ -f "$KUBECONFIG_PATH" ]]; then
    echo "Removing kubeconfig: $KUBECONFIG_PATH"
    rm -f "$KUBECONFIG_PATH"
fi

echo ""
echo "=== Cluster destroyed ==="
echo "To re-create: scripts/k8s-up.sh"
