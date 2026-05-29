#!/usr/bin/env bash
#
# scripts/infer-memberships-from-skills.sh — ADR-0026 Phase B legacy-upgrade
# helper. Backfills `queue_memberships` rows for the implicit skill-match pool
# that existed in pre-Phase-A.6 installs (when membership was decorative for
# digital channels — see ADR-0026 §"Phase B insertion point").
#
# Phase B makes `queue_memberships` the executive source of truth for routing
# across ALL channels. Agents who used to receive chats purely because their
# `agents.skills` JSONB intersected `queue_configs.required_skills` will stop
# receiving them once Phase B ships — unless an explicit membership row exists.
#
# This script inserts the missing rows (idempotent — ON CONFLICT DO NOTHING).
# Source='skill', Penalty=0, AllowedChannels=NULL (all channels the queue
# accepts, mirroring pre-v2.6.0 implicit behavior).
#
# Uso:
#   bash scripts/infer-memberships-from-skills.sh
#   bash scripts/infer-memberships-from-skills.sh --dry-run
#
# Variables de entorno reconocidas:
#   POSTGRES_CONTAINER   nombre del container Postgres        (default verbara-postgres)
#   POSTGRES_USER        usuario Postgres                     (default verbara)
#   POSTGRES_DB          base de datos                        (default verbara)
#   NO_COLOR=1           desactiva colores
#
# Sale con código:
#   0 — éxito (sin importar cuántas filas se insertaron, incluyendo 0)
#   1 — fallo (container no encontrado, psql error, etc.)
#   2 — uso inválido
#
# Idempotente: re-ejecutar el script no duplica filas (ON CONFLICT
# (tenant_id, queue_id, agent_id) DO NOTHING).

set -euo pipefail

POSTGRES_CONTAINER="${POSTGRES_CONTAINER:-verbara-postgres}"
POSTGRES_USER="${POSTGRES_USER:-verbara}"
POSTGRES_DB="${POSTGRES_DB:-verbara}"

DRY_RUN=false
case "${1:-}" in
    --dry-run) DRY_RUN=true ;;
    --help|-h)
        sed -n '2,33p' "$0"
        exit 0
        ;;
    "") ;;
    *)
        echo "error: argumento desconocido '$1'. usa --help" >&2
        exit 2
        ;;
esac

# ── Color helpers ──────────────────────────────────────────────────────────
if [[ -t 1 && -z "${NO_COLOR:-}" ]]; then
    BOLD=$'\e[1m'; GREEN=$'\e[32m'; YELLOW=$'\e[33m'; RED=$'\e[31m'; RESET=$'\e[0m'
else
    BOLD=""; GREEN=""; YELLOW=""; RED=""; RESET=""
fi

info()  { echo "${BOLD}[infer-memberships]${RESET} $*"; }
ok()    { echo "${GREEN}[infer-memberships]${RESET} $*"; }
warn()  { echo "${YELLOW}[infer-memberships]${RESET} $*" >&2; }
fail()  { echo "${RED}[infer-memberships]${RESET} $*" >&2; }

# ── Preflight ──────────────────────────────────────────────────────────────
if ! command -v docker >/dev/null 2>&1; then
    fail "docker no está en \$PATH"
    exit 1
fi

if ! docker ps --format '{{.Names}}' | grep -qx "$POSTGRES_CONTAINER"; then
    fail "container '$POSTGRES_CONTAINER' no encontrado o no corriendo."
    fail "Define POSTGRES_CONTAINER=<nombre> si tu container se llama distinto."
    exit 1
fi

info "container: $POSTGRES_CONTAINER · db: $POSTGRES_DB · user: $POSTGRES_USER"

# ── Query plan (dry-run) ───────────────────────────────────────────────────
PREVIEW_SQL="
SELECT COUNT(*) AS would_insert
FROM agents a
JOIN queue_configs q
  ON q.tenant_id = a.tenant_id
 AND q.is_active = TRUE
WHERE jsonb_typeof(q.required_skills) = 'array'
  AND jsonb_array_length(q.required_skills) > 0
  AND EXISTS (
      SELECT 1
      FROM jsonb_array_elements_text(a.skills) AS s(skill)
      WHERE s.skill IN (
          SELECT jsonb_array_elements_text(q.required_skills)
      )
  )
  AND NOT EXISTS (
      SELECT 1 FROM queue_memberships m
      WHERE m.tenant_id = a.tenant_id
        AND m.queue_id  = q.queue_id
        AND m.agent_id  = a.agent_id
  );
"

PREVIEW_OUT=$(docker exec -i "$POSTGRES_CONTAINER" \
    psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Atq -c "$PREVIEW_SQL" 2>&1) || {
    fail "psql preview falló:"
    echo "$PREVIEW_OUT" >&2
    exit 1
}

WOULD_INSERT="${PREVIEW_OUT:-0}"
info "filas (agent, queue) candidatas a backfill: $WOULD_INSERT"

if [[ "$DRY_RUN" == "true" ]]; then
    ok "--dry-run — no se ejecuta el INSERT. Sin cambios en queue_memberships."
    exit 0
fi

if [[ "$WOULD_INSERT" == "0" ]]; then
    ok "0 memberships inferred (todos los matches skill↔queue ya están en queue_memberships)."
    exit 0
fi

# ── Backfill ───────────────────────────────────────────────────────────────
INSERT_SQL="
INSERT INTO queue_memberships
    (tenant_id, queue_id, agent_id, penalty, source, is_excluded, created_at, allowed_channels)
SELECT
    a.tenant_id,
    q.queue_id,
    a.agent_id,
    0      AS penalty,
    'skill' AS source,
    FALSE  AS is_excluded,
    NOW()  AS created_at,
    NULL   AS allowed_channels
FROM agents a
JOIN queue_configs q
  ON q.tenant_id = a.tenant_id
 AND q.is_active = TRUE
WHERE jsonb_typeof(q.required_skills) = 'array'
  AND jsonb_array_length(q.required_skills) > 0
  AND EXISTS (
      SELECT 1
      FROM jsonb_array_elements_text(a.skills) AS s(skill)
      WHERE s.skill IN (
          SELECT jsonb_array_elements_text(q.required_skills)
      )
  )
ON CONFLICT (tenant_id, queue_id, agent_id) DO NOTHING
RETURNING tenant_id;
"

info "ejecutando backfill (transacción única, ON CONFLICT DO NOTHING)…"

INSERT_OUT=$(docker exec -i "$POSTGRES_CONTAINER" \
    psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Atq -c "$INSERT_SQL" 2>&1) || {
    fail "INSERT falló:"
    echo "$INSERT_OUT" >&2
    exit 1
}

INSERTED=$(echo "$INSERT_OUT" | grep -c . || true)
ok "$INSERTED memberships inferred (penalty=0, source='skill', allowed_channels=NULL)."
ok "Re-ejecuta cuando quieras — es idempotente."
