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

# 2. Copy local NuGet feed for Docker build (Pro packages)
echo "[2/9] Copiando NuGet feed local..."
PLATFORM_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
NUGET_FEED="/media/Data/Source/IPcom/local-nuget-feed"
if [ -d "$NUGET_FEED" ]; then
    mkdir -p "$PLATFORM_ROOT/local-nuget-feed"
    cp -r "$NUGET_FEED/"*.nupkg "$PLATFORM_ROOT/local-nuget-feed/" 2>/dev/null || true
    echo "  OK ($(ls "$PLATFORM_ROOT/local-nuget-feed/"*.nupkg 2>/dev/null | wc -l) packages)"
else
    echo "  SKIP (no local feed found at $NUGET_FEED)"
fi

# 3. Build images (if needed)
echo "[3/9] Construyendo imagenes..."
docker compose -f "$COMPOSE_FILE" build --quiet
echo "  OK"

# 4. Start Postgres
echo "[4/9] Iniciando Postgres..."
docker compose -f "$COMPOSE_FILE" up -d postgres
echo -n "  Esperando..."
until docker compose -f "$COMPOSE_FILE" exec -T postgres pg_isready -U platform -q 2>/dev/null; do
    echo -n "."
    sleep 1
done
echo " OK"

# 5. Start all services (Pro tables created by EnsureSchemaAsync during API DI registration)
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

# 7. Load seed data (after API has created Realtime tables via EnsureSchema)
echo "[7/9] Cargando datos seed..."
docker compose -f "$COMPOSE_FILE" exec -T postgres \
    psql -U platform -d platform -f /demo-sql/010_demo_asterisk_seed.sql -q
echo "  OK"

# 8. Insert historical demo data
echo "[8/9] Cargando datos historicos..."
docker compose -f "$COMPOSE_FILE" exec -T postgres \
    psql -U platform -d platform -f /demo-sql/020_demo_historical_data.sql -q
echo "  OK"

# 9. Warmup API + Summary
echo "[9/9] Verificando..."
curl -sf -X POST http://localhost:5000/api/auth/login \
    -H "Content-Type: application/json" \
    -d '{"tenantId":"demo","email":"admin@demo.local","password":"DemoAdmin2026!"}' > /dev/null 2>&1 || true
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
echo "    admin@demo.local             / DemoAdmin2026!      (System Admin)"
echo "    supervisor@demo.local        / DemoSupervisor2026! (Supervisor)"
echo "    maria.garcia@demo.local      / DemoAgent2026!      (Agent — ext 2001, sales)"
echo "    carlos.lopez@demo.local      / DemoAgent2026!      (Agent — ext 2002, sales)"
echo "    ana.martinez@demo.local      / DemoAgent2026!      (Agent — ext 2003, sales)"
echo "    pedro.ruiz@demo.local        / DemoAgent2026!      (Agent — ext 3001, support)"
echo "    lucia.fernandez@demo.local   / DemoAgent2026!      (Agent — ext 3002, support)"
echo "    demo.agent@demo.local        / DemoAgent2026!      (Agent — ext 3003, support)"
echo ""
echo "  IVR Espanol: marcar 200 desde softphone"
echo "  PSTN Test:   marcar 1001-1010"
echo "  Grafana:     admin / demo (o acceso anonimo)"
echo ""
