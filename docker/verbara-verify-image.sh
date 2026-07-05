#!/bin/sh
# verbara-verify-image.sh — pre-flight cosign verification for docker-compose deploys.
#
# Verifies that an OCI image was signed by the Verbara cosign keypair before the
# operator runs `docker compose up`. This is the docker-compose equivalent of the
# Kyverno admission policy shipped in infra/k8s/helm/platform/templates/cosign-
# admission-policy.yaml — Layer B of the Pro v2.3.x F+B+C defense stack
# (Pro/ADR-0011).
#
# Layer C (the in-process digest check inside Verbara.Sdk.Pro.Licensing) catches
# the same class of tampering at Pro startup. Running this script before
# `docker compose up` closes the gap that Pro cannot — namely, detecting a
# tampered image at pull time, before any code from it executes.
#
# Usage:
#   ./verbara-verify-image.sh ghcr.io/verbara/platform/api:v2.0.1
#
# Requirements:
#   - cosign (https://docs.sigstore.dev/cosign/system_config/installation/)
#   - docker buildx (ships with Docker Engine 20.10+ and Docker Desktop 4.0+)
#
# Exits non-zero on any verification failure. Stdout reports the resolved
# manifest-list digest so the operator can cross-check against the
# AuthorizedImageDigests claim of their license file (the same digest that the
# Verbara website issuer Worker embeds — see docs/operations/
# 2026-05-10-update-authorized-digests-after-release.md).

set -e

IMAGE="${1:-}"
if [ -z "$IMAGE" ]; then
  echo "Usage: $0 <image-ref>" >&2
  echo "Example: $0 ghcr.io/verbara/platform/api:v2.0.1" >&2
  exit 1
fi

# Require cosign and docker on PATH up front so the failure mode is obvious.
if ! command -v cosign >/dev/null 2>&1; then
  echo "Error: cosign not found on PATH." >&2
  echo "Install: https://docs.sigstore.dev/cosign/system_config/installation/" >&2
  exit 2
fi
if ! command -v docker >/dev/null 2>&1; then
  echo "Error: docker not found on PATH." >&2
  exit 2
fi

# Resolve the image reference to its OCI manifest-list digest. cosign's verify
# operates on the digest form, NOT the tag form (tags are mutable; digests are
# content-addressed).
DIGEST="$(docker buildx imagetools inspect "$IMAGE" --format '{{.Manifest.Digest}}' 2>/dev/null || true)"
if [ -z "$DIGEST" ]; then
  echo "Error: failed to resolve manifest-list digest for $IMAGE." >&2
  echo "Check that the tag exists in the registry and you have read access." >&2
  exit 3
fi

# Strip the tag from $IMAGE (split on ':') so we can recombine as IMAGE@DIGEST.
# This handles both `repo:tag` and `repo` (no tag) forms cleanly.
IMAGE_NO_TAG="$(echo "$IMAGE" | sed 's/:[^/]*$//')"
IMAGE_AT_DIGEST="${IMAGE_NO_TAG}@${DIGEST}"

echo "Verifying signature for ${IMAGE_AT_DIGEST} ..."

# Verify against the public key published at verbara.io/.well-known/cosign.pub.
# The Verbara release workflow signs with --tlog-upload=false, so we MUST pass
# --insecure-ignore-tlog here (the signature is offline-verifiable; this flag
# does NOT mean "trust anything" — it means "do not require a Sigstore
# transparency-log entry"). The signature itself is verified against the
# Verbara cosign public key, which is the actual root of trust.
cosign verify \
  --key https://verbara.io/.well-known/cosign.pub \
  --insecure-ignore-tlog \
  "$IMAGE_AT_DIGEST"

echo ""
echo "Signature valid. Safe to deploy."
echo "Manifest-list digest: ${DIGEST}"
echo ""
echo "Cross-check: this digest should appear in your license's"
echo "AuthorizedImageDigests claim. Decode with:"
echo "  jq -r '.payload' < your.lic | base64 -d | jq '.AuthorizedImageDigests'"
