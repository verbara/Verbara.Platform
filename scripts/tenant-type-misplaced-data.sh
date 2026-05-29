#!/usr/bin/env bash
# ADR-0027 — read-only inventory of operational rows that live on a non-Customer
# tenant (Platform or Partner). Run BEFORE flipping the RequireOperationalTenant
# gate on in a production-bound install so any pre-existing rows can be triaged
# (deleted if junk, UPDATE-d to the right Customer tenant if legitimate).
#
# On the SMB Docker happy path (Platform + 1×Customer) the output should be:
#   "0 misplaced rows across 9 operational tables."
#
# Usage:
#   bash scripts/tenant-type-misplaced-data.sh
#   POSTGRES_CONTAINER=my-postgres bash scripts/tenant-type-misplaced-data.sh
#   POSTGRES_USER=postgres POSTGRES_DB=verbara bash scripts/tenant-type-misplaced-data.sh
#
# Requirements:
#   - docker exec into a running Postgres container (default: verbara-postgres).
#   - Postgres credentials available as env vars OR via the container's existing
#     PGPASSWORD env / .pgpass.
#
# Output: one line per operational table reporting row count of misplaced rows,
# followed by a final summary line. Exit code 0 always; check the summary value
# to decide whether to triage before promoting the gate.

set -euo pipefail

POSTGRES_CONTAINER="${POSTGRES_CONTAINER:-verbara-postgres}"
POSTGRES_USER="${POSTGRES_USER:-platform}"
POSTGRES_DB="${POSTGRES_DB:-verbara}"

# Operational tables — these hold data that semantically belongs only to a
# Customer tenant. The list mirrors the OPERATIONAL endpoint groups gated by
# RequireOperationalTenant. Some tables may not exist in every install (e.g.
# dialer tables before Pro license is provisioned); errors are tolerated and
# reported as a separate count.
OPERATIONAL_TABLES=(
  agents
  queues
  queue_memberships
  channels_config
  campaigns
  flows
  bots
  articles
  surveys
)

# Build the WHERE-IN clause once: the set of NON-customer tenant_ids.
NON_CUSTOMER_QUERY="SELECT tenant_id FROM tenants WHERE type <> 2"

total_misplaced=0
missing_tables=0
echo "ADR-0027 — tenant-type misplacement inventory"
echo "container=${POSTGRES_CONTAINER} db=${POSTGRES_DB} user=${POSTGRES_USER}"
echo "================================================================"

run_query() {
  local sql="$1"
  docker exec "${POSTGRES_CONTAINER}" \
    psql -U "${POSTGRES_USER}" -d "${POSTGRES_DB}" -t -A -c "${sql}" 2>&1
}

for table in "${OPERATIONAL_TABLES[@]}"; do
  query="SELECT COUNT(*) FROM ${table} WHERE tenant_id IN (${NON_CUSTOMER_QUERY})"
  result=$(run_query "${query}" || echo "TABLE_MISSING")

  if [[ "${result}" == "TABLE_MISSING" ]] || echo "${result}" | grep -qi "does not exist"; then
    printf "  %-25s table missing (skipped)\n" "${table}"
    missing_tables=$((missing_tables + 1))
    continue
  fi

  count=$(echo "${result}" | tr -d '[:space:]')
  if [[ ! "${count}" =~ ^[0-9]+$ ]]; then
    printf "  %-25s ERROR: %s\n" "${table}" "${result}"
    continue
  fi

  total_misplaced=$((total_misplaced + count))
  if [[ "${count}" -gt 0 ]]; then
    printf "  %-25s %d misplaced rows\n" "${table}" "${count}"
    # Drill-down: show the offending tenant_id distribution
    drill="SELECT tenant_id, COUNT(*) FROM ${table}
             WHERE tenant_id IN (${NON_CUSTOMER_QUERY})
             GROUP BY tenant_id ORDER BY 2 DESC LIMIT 5"
    echo "    by tenant (top 5):"
    run_query "${drill}" | sed 's/^/      /'
  else
    printf "  %-25s 0\n" "${table}"
  fi
done

echo "================================================================"
echo "Summary: ${total_misplaced} misplaced rows across $((${#OPERATIONAL_TABLES[@]} - missing_tables)) operational tables."
if [[ "${missing_tables}" -gt 0 ]]; then
  echo "         (${missing_tables} table(s) skipped because they do not exist in this install)"
fi
if [[ "${total_misplaced}" -gt 0 ]]; then
  echo ""
  echo "Action required before enabling RequireOperationalTenant in production:"
  echo "  - Junk data (e.g. operator typed into the wrong tenant during testing): DELETE."
  echo "  - Legitimate Customer data placed under the wrong tenant: UPDATE ... SET tenant_id = <correct-customer-id>."
  echo "  - Tenants intentionally provisioned with operational data (none should exist by design):"
  echo "    re-classify them (UPDATE tenants SET type = 2 WHERE tenant_id = ...) if they are operational."
fi
