#!/bin/sh
# verbara-quickstart.sh — one-command verified Verbara Platform deploy for
# docker-compose customers (cosign pre-flight convenience wrapper, Pro/ADR-0011).
#
# Workflow:
#   1. Look up the requested Platform version's manifest-list digest from
#      verbara.io/data/authorized-digests.json (the release ledger; licences
#      issued since 2026-10-03 carry no image digests, verbara-meta/PDR-0003).
#   2. Pre-flight verify the cosign signature on that digest.
#   3. Generate a docker-compose.verified.yml with the resolved digests
#      substituted.
#   4. `docker compose pull` + `up -d`.
#
# Usage:
#   ./verbara-quickstart.sh v2.0.1
#
# Requirements:
#   - cosign, docker (with compose plugin), curl, jq.
#
# This script is convenience — power users may prefer to run the steps manually
# (./verbara-verify-image.sh + manual edit of docker-compose.verified.yml).

set -e

VERSION="${1:-}"
if [ -z "$VERSION" ]; then
  echo "Usage: $0 <platform-version>" >&2
  echo "Example: $0 v2.0.1" >&2
  exit 1
fi

for cmd in cosign docker curl jq; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "Error: $cmd not found on PATH." >&2
    exit 2
  fi
done

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
TEMPLATE="${SCRIPT_DIR}/docker-compose.verified.yml"
if [ ! -f "$TEMPLATE" ]; then
  echo "Error: template not found at $TEMPLATE" >&2
  echo "This script must live in the same directory as docker-compose.verified.yml." >&2
  exit 3
fi

REGISTRY_URL="https://verbara.io/data/authorized-digests.json"
echo "Fetching authorized digests from ${REGISTRY_URL} ..."
REGISTRY_JSON="$(curl -fsS "$REGISTRY_URL")"

# Find the entry matching the requested version. The ledger carries api and
# realtime entries (no web entry yet — see Pro/ADR-0011).
API_DIGEST="$(echo "$REGISTRY_JSON" \
  | jq -r --arg v "$VERSION" '.current[] | select(.platform_version == $v) | .manifest_list_digest' \
  | head -n 1)"

if [ -z "$API_DIGEST" ] || [ "$API_DIGEST" = "null" ]; then
  echo "Error: no entry for platform_version=${VERSION} in ${REGISTRY_URL}." >&2
  echo "Available versions:" >&2
  echo "$REGISTRY_JSON" | jq -r '.current[].platform_version' | sed 's/^/  /' >&2
  exit 4
fi

API_IMAGE_REF="ghcr.io/verbara/platform/api:${VERSION}"
API_PINNED_REF="ghcr.io/verbara/platform/api@${API_DIGEST}"

echo "Resolved ${VERSION} -> ${API_DIGEST}"
echo "Verifying cosign signature ..."
cosign verify \
  --key https://verbara.io/.well-known/cosign.pub \
  --insecure-ignore-tlog \
  "$API_PINNED_REF"

# TODO(web-digest-pin): the web image is not in authorized-digests.json.
# Until it is, the web service is left tag-pinned in the generated compose and is NOT
# verified. Operators who want to verify the web image manually can run
# `./verbara-verify-image.sh ghcr.io/verbara/platform/web:${VERSION}` before
# `docker compose up`.

GENERATED="${SCRIPT_DIR}/docker-compose.verified.${VERSION}.yml"
echo "Generating ${GENERATED} ..."

# Substitute the resolved digest. Use a sed alternative that handles the `/`
# in the digest cleanly by anchoring on the literal sentinel. THREE substitutions:
#   1. image: line for the API service (digest-pinned image reference)
#   2. IMAGE_DIGEST env var (the running image's digest, for Pro, Pro/ADR-0011;
#      Pro can't self-discover the running image's digest from inside the
#      container — operator-side env var is the canonical wiring)
#   3. image: line for the Web service (still tag-pinned per
#      TODO(web-digest-pin) above)
sed \
  -e "s|ghcr.io/verbara/platform/api@sha256:REPLACE_WITH_MANIFEST_LIST_DIGEST|${API_PINNED_REF}|g" \
  -e "s|IMAGE_DIGEST: sha256:REPLACE_WITH_MANIFEST_LIST_DIGEST|IMAGE_DIGEST: ${API_DIGEST}|g" \
  -e "s|ghcr.io/verbara/platform/web@sha256:REPLACE_WITH_WEB_MANIFEST_LIST_DIGEST|ghcr.io/verbara/platform/web:${VERSION}|g" \
  "$TEMPLATE" > "$GENERATED"

echo ""
echo "Generated docker-compose file: ${GENERATED}"
echo ""
echo "Next steps:"
echo "  1. Place your Verbara Pro license file at: ${SCRIPT_DIR}/license.lic"
echo "  2. Review ${GENERATED} (especially environment vars + volume mounts)"
echo "  3. Bring up the stack:"
echo "       docker compose -f ${GENERATED} pull"
echo "       docker compose -f ${GENERATED} up -d"
echo ""
echo "If the deploy fails with 'no matching manifest', the registry rotated"
echo "the digest behind your version tag. Re-run this script to refresh."
