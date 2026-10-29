# Verbara Platform Helm Chart

Deploys `platform-api` + `platform-web` to Kubernetes with optional
cosign-based image verification.

## Quick start (ghcr.io defaults)

```sh
helm install platform infra/k8s/helm/platform/
```

The chart's defaults pin the current release (`api` and `realtime`
`v2.34.1` by tag and manifest-list digest, `web` `v3.27.1-web`). To deploy
another release, override the tag **and** the digest of each image you
change, for example
`--set api.image.tag=vX.Y.Z --set api.image.digest=sha256:...`; when a
digest is set, the kubelet pulls by digest and the tag is informational.

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
  ghcr.io/verbara/platform/api:v2.34.1
```

This is the same check the admission webhook runs internally. The
`--insecure-ignore-tlog` flag is required because the Verbara release
workflow signs with `--tlog-upload=false` (offline-verifiable signature,
matches the `.well-known/cosign.pub` flow used by the docker-compose
`verbara-verify-image.sh` script).

## Image integrity controls

| Control | Where it runs | What it catches |
|---------|---------------|-----------------|
| Cosign signature | Every release image is signed by `.github/workflows/release.yml`; you verify it with this chart's opt-in Kyverno policy (above) or the cosign CLI | Unsigned or tampered images |
| Digest pin | `api.image.digest` / `realtime.image.digest`: the kubelet pulls those exact bytes | A tag that was moved to different content |
| Licence signature | Verbara Pro licensing, inside the api | A licence file that Verbara did not issue |

**A licence is not tied to an image.** Licences issued by Verbara do not
carry image digests (verbara-meta/PDR-0003, amendment of 2026-10-03), so
upgrading the image never requires a new licence. Image integrity is the
job of the cosign signature and the digest pin. Verbara publishes every
release's `api` + `realtime` digests in the release notes, and each one
is recorded in the release ledger `verbara-website/data/authorized-digests.json`.

**Verifying the signature at admission is an optional operator control.**
The policy is off by default (`imageVerification.enabled: false`). Turn it
on where Kyverno 1.11 or later runs (see *Verifying image signature* above);
where no admission controller is available, verify out of band with the
cosign CLI before you deploy.

Licences issued before 2026-10-03 may still list image digests. The api
keeps checking such a licence against the running image, so with one of
those licences an upgrade to a newer image needs a current licence from
Verbara.

For the docker-compose equivalent of the admission check, see
`docker/verbara-verify-image.sh` and `docker/docker-compose.verified.yml`.
