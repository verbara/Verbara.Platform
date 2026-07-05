# Verbara Platform Helm Chart

Deploys `platform-api` + `platform-web` to Kubernetes with optional
cosign-based image verification.

## Quick start (ghcr.io defaults)

```sh
helm install platform infra/k8s/helm/platform/ \
  --set api.image.tag=v2.0.1 \
  --set web.image.tag=v2.0.1
```

The chart defaults `api.image.repository` and `web.image.repository` to
`ghcr.io/verbara/platform/api` and `ghcr.io/verbara/platform/web` per
Pro/ADR-0011. These are the canonical published images signed by the Verbara
cosign keypair.

## Local KVM dev cluster override

The maintainer's k3s/Talos lab pushes images to a local registry on
`192.168.122.1:5050`. To deploy against that registry instead of
`ghcr.io`, override the image repository:

```sh
helm install platform infra/k8s/helm/platform/ \
  --set api.image.repository=192.168.122.1:5050/verbara-platform/api \
  --set web.image.repository=192.168.122.1:5050/verbara-platform/web \
  --set imageVerification.enabled=false
```

The local registry images are not cosign-signed (they're development
artefacts), so `imageVerification.enabled` MUST stay `false` for local
KVM deploys; otherwise admission rejects every pod.

## Verifying image signature (Kyverno)

The chart ships an opt-in Kyverno `ClusterPolicy` that requires every
Pod whose image matches `ghcr.io/verbara/platform/*` to carry a valid
cosign signature from the Verbara cosign keypair.

### Prerequisites

Kyverno 1.11 or later installed in the cluster:

```sh
helm repo add kyverno https://kyverno.github.io/kyverno/
helm repo update
helm install kyverno kyverno/kyverno -n kyverno --create-namespace
```

### Enable verification

```sh
helm install platform infra/k8s/helm/platform/ \
  --set imageVerification.enabled=true \
  --set-file imageVerification.cosignPublicKey=infra/k8s/helm/platform/files/cosign.pub
```

After this, an unsigned image (or one signed by a different cosign key)
is rejected at admission time with:

```
admission webhook "validate.kyverno.svc-fail" denied the request:
  resource Pod/<namespace>/<pod-name> was blocked due to the following policies:
    platform-platform-cosign-verify:
      verify-platform-image: 'failed to verify signature: ...'
```

### Verifying out-of-band (cosign CLI)

```sh
cosign verify \
  --key infra/k8s/helm/platform/files/cosign.pub \
  --insecure-ignore-tlog \
  ghcr.io/verbara/platform/api:v2.0.1
```

This is the same check the admission webhook runs internally. The
`--insecure-ignore-tlog` flag is required because the Verbara release
workflow signs with `--tlog-upload=false` (offline-verifiable signature,
matches the `.well-known/cosign.pub` flow used by the docker-compose
`verbara-verify-image.sh` script).

## Defense-in-depth model (Pro/ADR-0011)

| Layer | Where it runs | What it catches |
|-------|---------------|-----------------|
| F (ECDSA license) | Pro v2.2.0-pro `LicenseTrustAnchor` | Forged license payloads |
| **B (cosign signature)** | This chart's Kyverno policy | Unsigned/tampered images at admission time |
| **C (in-process digest check)** | `Verbara.Sdk.Pro.Licensing.ContainerImageDigest` | Authorised-image mismatch at Pro startup |

Layer B (this chart) and Layer C (Pro v2.3.x) reinforce each other.
Disable Layer B if your cluster has no admission-policy controller; Layer
C still works because Pro reads `/etc/verbara-image-digest` directly from
the container filesystem.

For the docker-compose equivalent of Layer B, see
`docker/verbara-verify-image.sh` and `docker/docker-compose.verified.yml`.
