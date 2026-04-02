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
echo "[1/11] Limpiando entorno anterior..."
docker compose -f "$COMPOSE_FILE" down -v --remove-orphans 2>/dev/null || true
echo "  OK"

# 2. Copy local NuGet feed for Docker build (Pro packages)
echo "[2/11] Copiando NuGet feed local..."
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
echo "[3/11] Construyendo imagenes..."
docker compose -f "$COMPOSE_FILE" build --quiet
echo "  OK"

# 4. Start Postgres
echo "[4/11] Iniciando Postgres..."
docker compose -f "$COMPOSE_FILE" up -d postgres
echo -n "  Esperando..."
until docker compose -f "$COMPOSE_FILE" exec -T postgres pg_isready -U platform -q 2>/dev/null; do
    echo -n "."
    sleep 1
done
echo " OK"

# 5. Start all services (Pro tables created by EnsureSchemaAsync during API DI registration)
echo "[5/11] Iniciando todos los servicios..."
docker compose -f "$COMPOSE_FILE" up -d
echo "  OK"

# 6. Wait for all services healthy
echo "[6/11] Esperando servicios..."
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

API_BASE="http://localhost:5000"

# 7. Initialize platform via setup wizard (creates host tenant + admin + mgmt key)
echo "[7/11] Inicializando plataforma..."
SETUP_RESPONSE=$(curl -sf -X POST "$API_BASE/api/setup" \
    -H "Content-Type: application/json" \
    -d '{
        "email": "platform@admin.local",
        "password": "PlatformAdmin2026!",
        "displayName": "Platform Admin",
        "platformName": "Asterisk Platform"
    }' 2>/dev/null || echo '{"error":"setup failed or already initialized"}')
MGMT_KEY=$(echo "$SETUP_RESPONSE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('managementApiKey',''))" 2>/dev/null || echo "")
if [ -n "$MGMT_KEY" ]; then
    echo "  OK (management key: ${MGMT_KEY:0:20}...)"
else
    echo "  SKIP (platform already initialized or setup failed)"
fi

# 8. Create demo customer tenant via Management API
echo "[8/11] Creando tenant demo..."
if [ -n "$MGMT_KEY" ]; then
    curl -sf -X POST "$API_BASE/api/management/tenants" \
        -H "Content-Type: application/json" \
        -H "Authorization: Bearer $MGMT_KEY" \
        -d '{"tenantId":"demo","name":"Demo Contact Center","type":2}' > /dev/null 2>&1 || true
    echo "  OK (tenant 'demo' created as child of platform)"
else
    echo "  SKIP (no management key)"
fi

# 8.5. Verify cluster status and register PSTN emulator as secondary node
echo "[8.5/11] Verificando cluster y registrando nodos..."
if [ -n "$MGMT_KEY" ]; then
    # Primary node is auto-registered via InitialNodes config — verify it's visible
    CLUSTER_NODES=$(curl -sf "$API_BASE/api/management/cluster/status" \
        -H "Authorization: Bearer $MGMT_KEY" 2>/dev/null || echo "{}")
    echo "  Cluster status: $CLUSTER_NODES" | head -c 200
    echo ""

    # Register PSTN emulator as secondary cluster node
    curl -sf -X POST "$API_BASE/api/management/cluster/nodes" \
        -H "Content-Type: application/json" \
        -H "Authorization: Bearer $MGMT_KEY" \
        -d '{
            "nodeId": "pstn-emulator",
            "amiHostname": "pstn-emulator",
            "amiPort": 5038,
            "amiUsername": "platform",
            "amiPassword": "'"${AMI_PASSWORD:-platform_demo}"'",
            "weight": 0.5,
            "priorityTier": 1,
            "maxCapacity": 100,
            "tags": {"role": "pstn-gateway"}
        }' > /dev/null 2>&1 && echo "  OK (pstn-emulator registered as cluster node)" || echo "  SKIP (pstn-emulator already registered or unavailable)"
else
    echo "  SKIP (no management key)"
fi

# 9. Seed demo data via API (persisted to Postgres when connection string is configured)
echo "[9/11] Creando datos demo via API..."

# Get a JWT for the platform admin to use admin endpoints
PLATFORM_JWT=$(curl -sf -X POST "$API_BASE/api/auth/login" \
    -H "Content-Type: application/json" \
    -d '{"tenantId":"platform","email":"platform@admin.local","password":"PlatformAdmin2026!"}' \
    | python3 -c "import sys,json; print(json.load(sys.stdin).get('accessToken',''))" 2>/dev/null || echo "")

if [ -z "$PLATFORM_JWT" ]; then
    echo "  ERROR: Could not obtain platform JWT"
else
    AUTH="Authorization: Bearer $PLATFORM_JWT"
    TENANT="X-Tenant-Id: demo"
    CT="Content-Type: application/json"

    # Create demo admin user
    curl -sf -X POST "$API_BASE/api/admin/users" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"userId":"demo-user-admin","email":"admin@demo.local","displayName":"Demo Admin","role":"Admin","password":"DemoAdmin2026!"}' > /dev/null 2>&1 || true
    # Create demo supervisor
    curl -sf -X POST "$API_BASE/api/admin/users" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"userId":"demo-user-supervisor","email":"supervisor@demo.local","displayName":"Demo Supervisor","role":"Supervisor","password":"DemoSupervisor2026!"}' > /dev/null 2>&1 || true

    # Create 6 agent users + agent records
    for agent in \
        'demo-user-maria|maria.garcia@demo.local|Maria Garcia|demo-agent-maria|2001|demo2001|sales' \
        'demo-user-carlos|carlos.lopez@demo.local|Carlos Lopez|demo-agent-carlos|2002|demo2002|sales' \
        'demo-user-ana|ana.martinez@demo.local|Ana Martinez|demo-agent-ana|2003|demo2003|sales' \
        'demo-user-pedro|pedro.ruiz@demo.local|Pedro Ruiz|demo-agent-pedro|3001|demo3001|support' \
        'demo-user-lucia|lucia.fernandez@demo.local|Lucia Fernandez|demo-agent-lucia|3002|demo3002|support' \
        'demo-user-demo|demo.agent@demo.local|Demo Agent|demo-agent-demo|3003|demo3003|support'
    do
        IFS='|' read -r uid email name aid ext sippwd skill <<< "$agent"
        # Create user
        curl -sf -X POST "$API_BASE/api/admin/users" -H "$CT" -H "$AUTH" -H "$TENANT" \
            -d "{\"userId\":\"$uid\",\"email\":\"$email\",\"displayName\":\"$name\",\"role\":\"Agent\",\"password\":\"DemoAgent2026!\"}" > /dev/null 2>&1 || true
        # Create agent
        curl -sf -X POST "$API_BASE/api/admin/agents" -H "$CT" -H "$AUTH" -H "$TENANT" \
            -d "{\"agentId\":\"$aid\",\"userId\":\"$uid\",\"displayName\":\"$name\",\"extension\":\"$ext\",\"sipPassword\":\"$sippwd\",\"skills\":[\"$skill\"]}" > /dev/null 2>&1 || true
    done

    # Create queues
    curl -sf -X POST "$API_BASE/api/admin/queues" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"queueId":"demo-queue-sales","name":"Sales","isActive":true}' > /dev/null 2>&1 || true
    curl -sf -X POST "$API_BASE/api/admin/queues" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"queueId":"demo-queue-support","name":"Support","isActive":true}' > /dev/null 2>&1 || true

    # Activate WebChat channel
    curl -sf -X PUT "$API_BASE/api/admin/channels/webchat" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"isActive":true,"credentials":{}}' > /dev/null 2>&1 || true

    echo "  OK (admin, supervisor, 6 agents, 2 queues, webchat channel)"
fi

# 10. Load Asterisk Realtime seed + historical data (Pro-owned Postgres tables)
echo "[10/11] Cargando datos Asterisk + historicos..."
docker compose -f "$COMPOSE_FILE" exec -T postgres \
    psql -U platform -d platform -f /demo-sql/010_demo_asterisk_seed.sql -q
docker compose -f "$COMPOSE_FILE" exec -T postgres \
    psql -U platform -d platform -f /demo-sql/020_demo_historical_data.sql -q
echo "  OK"

# 11. Warmup + Summary
echo "[11/11] Verificando..."
LOGIN_RESULT=$(curl -sf -X POST "$API_BASE/api/auth/login" \
    -H "Content-Type: application/json" \
    -d '{"tenantId":"demo","email":"admin@demo.local","password":"DemoAdmin2026!"}' 2>/dev/null)
if echo "$LOGIN_RESULT" | grep -q "accessToken"; then
    echo "  Login: OK"
else
    echo "  Login: FAILED ($(echo "$LOGIN_RESULT" | head -c 80))"
fi
API_STATUS=$(curl -sf "$API_BASE/health" 2>/dev/null | head -c 50 || echo "unreachable")
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
echo "  Platform Admin (host tenant: platform):"
echo "    platform@admin.local         / PlatformAdmin2026!  (Platform Admin)"
if [ -n "$MGMT_KEY" ]; then
echo "    Management API Key:          $MGMT_KEY"
else
echo "    Management API Key:          (run POST /api/setup to generate)"
fi
echo ""
echo "  Demo Tenant (customer, child of platform):"
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
