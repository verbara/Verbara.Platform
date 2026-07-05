# Verbara Platform — Docker assets

This directory ships docker-compose files for various deployment shapes
(`docker-compose.full.yml`, `docker-compose.production.yml`,
`docker-compose.smb.yml`, `docker-compose.scale.yml`, …) plus the
Asterisk container build (`Dockerfile.asterisk` + `entrypoint-asterisk.sh`)
and observability sidecars (`docker-compose.observability.yml`).

## Verifying image signature (Docker Compose)

Pro v2.3.x ships image-binding (Pro/ADR-0011)
that detects unauthorised Verbara Platform images at runtime via Layer C
(in-process digest check). The docker-compose tooling here adds Layer B
(pre-flight cosign signature verification) so customers catch a tampered
image at pull time, before any container code runs.

### Quick path: `verbara-quickstart.sh`

One command, end-to-end: look up the digest, verify the signature,
generate a digest-pinned compose file, pull, and bring the stack up.

```sh
cd docker/
./verbara-quickstart.sh v2.0.1
docker compose -f docker-compose.verified.v2.0.1.yml pull
docker compose -f docker-compose.verified.v2.0.1.yml up -d
```

Requires: `cosign`, `docker`, `curl`, `jq`.

### Power-user path: manual

If you prefer to run each step by hand, or want to verify a specific
image without touching the registry:

```sh
# 1. Verify the cosign signature
./verbara-verify-image.sh ghcr.io/verbara/platform/api:v2.0.1
# (prints the resolved manifest-list digest on success)

# 2. Edit docker-compose.verified.yml — replace
#    sha256:REPLACE_WITH_MANIFEST_LIST_DIGEST with the digest from step 1.

# 3. Bring up the stack
docker compose -f docker-compose.verified.yml pull
docker compose -f docker-compose.verified.yml up -d
```

### Why this matters

Without running `verbara-verify-image.sh` (or the `quickstart` wrapper),
docker-compose customers get only the **in-process Layer-C check** —
which is still a real defense (Pro rejects unauthorised images at
startup) but does NOT detect a tampered image being **pulled** in the
first place. Pre-flight verification closes that gap.

The Kyverno admission policy that K8s customers get via the Helm chart
(`infra/k8s/helm/platform/templates/cosign-admission-policy.yaml`)
enforces the equivalent guarantee — a tampered image is rejected at Pod
admission, before kubelet pulls the layers. This script provides the
docker-compose equivalent.

### Defense-in-depth (Pro/ADR-0011)

| Layer | Where it runs | What it catches |
|-------|---------------|-----------------|
| F (ECDSA license) | Pro v2.2.0-pro `LicenseTrustAnchor` | Forged license payloads |
| **B (cosign signature)** | `verbara-verify-image.sh` (or Kyverno on K8s) | Unsigned/tampered images at pull/admission time |
| **C (in-process digest check)** | `Verbara.Sdk.Pro.Licensing.ContainerImageDigest` | Authorised-image mismatch at Pro startup |

### Files

| File | Purpose |
|------|---------|
| `verbara-verify-image.sh` | Pre-flight cosign verify for a single image ref. |
| `verbara-smoke-released.sh` | Post-release FUNCTIONAL smoke: composes the released (digest-pinned) images and runs one end-to-end journey (`/health/ready` binary polling + setup→login). `--local` builds the demo images instead — dev-only, does NOT verify the actually-released artifact. |
| `verbara-quickstart.sh` | End-to-end wrapper: lookup -> verify -> generate -> pull -> up. |
| `docker-compose.verified.yml` | Template — operators substitute their resolved digest. |
| `docker-compose.full.yml` | Existing dev/demo full-stack compose (Asterisk + API + Web + storage). |
| `docker-compose.production.yml` | Existing production compose (no dev seeds, external storage assumed). |

### Operator runbook

After every Verbara Platform tagged release, the maintainer must update
the digest registry that powers `verbara-quickstart.sh`. See
[`docs/operations/2026-05-10-update-authorized-digests-after-release.md`](../docs/operations/2026-05-10-update-authorized-digests-after-release.md).
