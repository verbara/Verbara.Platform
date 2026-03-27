#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
COMPOSE_FILE="$SCRIPT_DIR/docker-compose.demo.yml"
ENV_FILE="$SCRIPT_DIR/.env.demo"

# Load env
set -a
source "$ENV_FILE"
set +a

echo "============================================"
echo "  Asterisk Platform — Demo Reset"
echo "============================================"
echo ""

# 1. Clean up
echo "[1/9] Limpiando entorno anterior..."
docker compose -f "$COMPOSE_FILE" down -v --remove-orphans 2>/dev/null || true
echo "  OK"

# 2. Build images (if needed)
echo "[2/9] Construyendo imagenes..."
docker compose -f "$COMPOSE_FILE" build --quiet
echo "  OK"

# 3. Start Postgres
echo "[3/9] Iniciando Postgres..."
docker compose -f "$COMPOSE_FILE" up -d postgres
echo -n "  Esperando..."
until docker compose -f "$COMPOSE_FILE" exec -T postgres pg_isready -U platform -q 2>/dev/null; do
    echo -n "."
    sleep 1
done
echo " OK"

# 4. Run Asterisk seed SQL (extensions, queues, trunks, IVR)
echo "[4/9] Cargando datos Asterisk..."
# Wait for migrations to complete (docker-entrypoint-initdb.d runs on first start)
sleep 3
docker compose -f "$COMPOSE_FILE" exec -T postgres \
    psql -U platform -d platform -f /demo-sql/010_demo_asterisk_seed.sql -q
echo "  OK"

# 5. Start all services
echo "[5/9] Iniciando todos los servicios..."
docker compose -f "$COMPOSE_FILE" up -d
echo "  OK"

# 6. Wait for all services healthy
echo "[6/9] Esperando servicios..."
for svc in asterisk pstn-emulator platform-api web grafana; do
    echo -n "  $svc..."
    timeout=120
    elapsed=0
    while true; do
        health=$(docker compose -f "$COMPOSE_FILE" ps "$svc" --format '{{.Health}}' 2>/dev/null || echo "unknown")
        if [ "$health" = "healthy" ]; then
            echo " OK"
            break
        fi
        if [ $elapsed -ge $timeout ]; then
            echo " TIMEOUT (continuing)"
            break
        fi
        sleep 2
        elapsed=$((elapsed + 2))
    done
done

# 7. Insert historical demo data (requires Pro tables from API startup)
echo "[7/9] Cargando datos historicos..."
sleep 5  # Give API time to create Pro tables
docker compose -f "$COMPOSE_FILE" exec -T postgres \
    psql -U platform -d platform -f /demo-sql/020_demo_historical_data.sql -q
echo "  OK"

# 8. Warmup API
echo "[8/9] Pre-calentando API..."
curl -sf -X POST http://localhost:5000/api/auth/login \
    -H "Content-Type: application/json" \
    -d '{"email":"admin@demo.local","password":"Admin123!"}' > /dev/null 2>&1 || true
echo "  OK"

# 9. Summary
echo "[9/9] Verificando..."
API_STATUS=$(curl -sf http://localhost:5000/health 2>/dev/null | head -c 50 || echo "unreachable")
echo "  API: $API_STATUS"
echo ""
echo "============================================"
echo "  Demo Ready!"
echo "============================================"
echo ""
echo "  Platform Web:  http://localhost"
echo "  Platform API:  http://localhost:5000"
echo "  Grafana:       http://localhost:3000"
echo "  Prometheus:    http://localhost:9090"
echo ""
echo "  Usuarios:"
echo "    admin@demo.local      / Admin123!      (System Admin + MFA)"
echo "    supervisor@demo.local / Supervisor123!  (Supervisor)"
echo "    agent@demo.local      / Agent123!       (Agent + Softphone)"
echo ""
echo "  IVR Espanol: marcar 200 desde softphone"
echo "  PSTN Test:   marcar 1001-1010"
echo "  Grafana:     admin / demo (o acceso anonimo)"
echo ""
