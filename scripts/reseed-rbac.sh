#!/usr/bin/env bash
# R5.2 PC.3 (B.7) — convenience wrapper over the RbacReseed CLI tool.
#
# Re-aligns the per-tenant `platform_admin` role permissions with the current
# canonical permission catalog. Required after deploying a release that adds
# new permission keys (e.g. R5.2 added 7 keys for security/audit/retention).
#
# Usage:
#   ./scripts/reseed-rbac.sh --tenants tenant-a,tenant-b
#   ./scripts/reseed-rbac.sh --all
#
# Reads CONNECTION_STRING from env or .env if present. The CLI also accepts
# --connection / -c overrides which are forwarded to the tool.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

# Allow .env override (only if no explicit --connection passed).
if [[ -f "${ROOT_DIR}/.env" ]]; then
  # shellcheck disable=SC1091
  source "${ROOT_DIR}/.env"
fi

# Skip the connection guard for help/no-args paths so usage is always printable.
NEEDS_CONN=true
for arg in "$@"; do
  case "$arg" in
    --help|-h) NEEDS_CONN=false; break ;;
  esac
done

if [[ "$NEEDS_CONN" == "true" ]] && [[ -z "${CONNECTION_STRING:-}" ]] \
   && ! [[ "$*" == *"--connection"* || "$*" == *"-c "* ]]; then
  echo "Error: CONNECTION_STRING env var required (or pass --connection)." >&2
  echo "  Example: 'Host=localhost;Database=asterisk_platform;Username=postgres;Password=postgres'" >&2
  exit 1
fi

cd "${ROOT_DIR}"
exec dotnet run --project tools/RbacReseed -- "$@"
