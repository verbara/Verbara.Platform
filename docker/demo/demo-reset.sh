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
echo "  Verbara Platform — Demo Reset"
echo "============================================"
echo ""

# 1. Clean up
echo "[1/11] Limpiando entorno anterior..."
docker compose -f "$COMPOSE_FILE" down -v --remove-orphans 2>/dev/null || true
echo "  OK"

# 2. Copy local NuGet feed for Docker build (Pro packages)
echo "[2/11] Copiando NuGet feed local..."
PLATFORM_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
NUGET_FEED="$(cd "$PLATFORM_ROOT/../local-nuget-feed" 2>/dev/null && pwd || true)"
if [ -z "$NUGET_FEED" ] || [ ! -d "$NUGET_FEED" ]; then
    echo "  ERROR: local NuGet feed not found at $PLATFORM_ROOT/../local-nuget-feed" >&2
    echo "  Expected sibling 'local-nuget-feed/' next to the Verbara repos (see workspace CLAUDE.md)." >&2
    exit 1
fi
mkdir -p "$PLATFORM_ROOT/local-nuget-feed"
cp -r "$NUGET_FEED/"*.nupkg "$PLATFORM_ROOT/local-nuget-feed/" 2>/dev/null || true
echo "  OK ($(ls "$PLATFORM_ROOT/local-nuget-feed/"*.nupkg 2>/dev/null | wc -l) packages)"

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

# 7. Initialize platform via setup wizard. Since v2.6.0 the setup endpoint
# creates BOTH the host "platform" tenant (admin) AND a first operational
# "Customer" tenant (admin) in one call — Platform is administrative-only and
# cannot hold agents/queues (ADR-0027), so a Customer is mandatory. We make
# that first Customer the "demo" tenant the rest of this script seeds into;
# step 8 below then becomes an idempotent no-op (the tenant already exists).
echo "[7/11] Inicializando plataforma (platform + customer 'demo')..."
SETUP_RESPONSE=$(curl -sf -X POST "$API_BASE/api/v1/setup" \
    -H "Content-Type: application/json" \
    -d '{
        "email": "platform@admin.local",
        "password": "PlatformAdmin2026!",
        "displayName": "Platform Admin",
        "platformName": "Verbara Platform",
        "customerTenantId": "demo",
        "customerName": "Demo Contact Center",
        "customerAdminEmail": "admin@demo.local",
        "customerAdminPassword": "DemoAdmin2026!",
        "customerAdminDisplayName": "Demo Admin"
    }' 2>/dev/null || echo '{"error":"setup failed or already initialized"}')
MGMT_KEY=$(echo "$SETUP_RESPONSE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('managementApiKey',''))" 2>/dev/null || echo "")
if [ -n "$MGMT_KEY" ]; then
    echo "  OK (management key: ${MGMT_KEY:0:20}...)"
else
    echo "  SKIP (platform already initialized or setup failed)"
fi

# 7.5. Ensure platform admin user has the `platform_admin` role assigned.
# SetupEndpoints does this on first-run, but Postgres-backed demos that
# initialized before that fix landed still have an empty user_roles row for
# platform@admin.local. This block is idempotent: clones the template into a
# tenant_role (ignoring unique-constraint errors on re-run) and upserts the
# user_role assignment. Runs inside Postgres so it works even when the setup
# endpoint returned 409 Conflict.
echo "[7.5/11] Asignando platform_admin role al platform admin (idempotente)..."
docker exec -i demo-postgres-1 psql -U platform -d platform >/dev/null 2>&1 <<'SQL' || true
    -- 1. Clone platform_admin template into a tenant role (if not already present)
    INSERT INTO tenant_roles (tenant_id, role_id, name, description, source_template_id, is_default, created_at)
    SELECT 'platform', 'platform-admin-platform', 'Platform Admin',
           'Full platform administration including cross-tenant operations',
           'platform_admin', false, NOW()
    WHERE NOT EXISTS (
        SELECT 1 FROM tenant_roles
        WHERE tenant_id = 'platform' AND role_id = 'platform-admin-platform'
    );

    -- 2. Copy template's permissions into tenant_role_permissions
    INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id)
    SELECT 'platform', 'platform-admin-platform', tp.permission_id
    FROM template_permissions tp
    WHERE tp.template_id = 'platform_admin'
    ON CONFLICT DO NOTHING;

    -- 3. Assign the role to every user on the platform tenant (admins)
    INSERT INTO user_roles (tenant_id, user_id, role_id, assigned_at, assigned_by)
    SELECT 'platform', u.user_id, 'platform-admin-platform', NOW(), NULL
    FROM users u
    WHERE u.tenant_id = 'platform'
      AND NOT EXISTS (
          SELECT 1 FROM user_roles ur
          WHERE ur.tenant_id = 'platform'
            AND ur.user_id = u.user_id
            AND ur.role_id = 'platform-admin-platform'
      );
SQL
echo "  OK"

# 8. Ensure demo customer tenant exists (idempotent). Since v2.6.0 step 7's
# setup already creates "demo" as the first Customer; this POST is a no-op
# safety net (409 swallowed by `|| true`) for re-runs where setup returned 409.
echo "[8/11] Asegurando tenant demo (idempotente)..."
if [ -n "$MGMT_KEY" ]; then
    curl -sf -X POST "$API_BASE/api/v1/management/tenants" \
        -H "Content-Type: application/json" \
        -H "Authorization: Bearer $MGMT_KEY" \
        -d '{"tenantId":"demo","name":"Demo Contact Center","type":2}' > /dev/null 2>&1 || true
    echo "  OK (tenant 'demo' present as child of platform)"
else
    echo "  SKIP (no management key)"
fi

# 8.5. Set tenant plans (platform=Enterprise, demo=Pro) to enable feature-gated endpoints
echo "[8.5/11] Configurando planes (platform=Enterprise, demo=Pro)..."
if [ -n "$MGMT_KEY" ]; then
    curl -sf -X PUT "$API_BASE/api/v1/management/tenants/platform/settings" \
        -H "Content-Type: application/json" \
        -H "Authorization: Bearer $MGMT_KEY" \
        -d '{"plan":"Enterprise"}' > /dev/null 2>&1 || true
    curl -sf -X PUT "$API_BASE/api/v1/management/tenants/demo/settings" \
        -H "Content-Type: application/json" \
        -H "Authorization: Bearer $MGMT_KEY" \
        -d '{"plan":"Pro"}' > /dev/null 2>&1 || true
    echo "  OK (platform=Enterprise all features, demo=Pro: Dialer, BotBasic, AnalyticsExport, Flows, Webhooks, ScheduledReports, KnowledgeBase, Recordings)"
else
    echo "  SKIP (no management key)"
fi

# 9. Seed demo data via API (persisted to Postgres when connection string is configured)
echo "[9/11] Creando datos demo via API..."

# Get a JWT for the platform admin to use admin endpoints
PLATFORM_JWT=$(curl -sf -X POST "$API_BASE/api/v1/auth/login" \
    -H "Content-Type: application/json" \
    -d '{"tenantId":"platform","email":"platform@admin.local","password":"PlatformAdmin2026!"}' \
    | python3 -c "import sys,json; print(json.load(sys.stdin).get('accessToken',''))" 2>/dev/null || echo "")

if [ -z "$PLATFORM_JWT" ]; then
    echo "  ERROR: Could not obtain platform JWT"
else
    AUTH="Authorization: Bearer $PLATFORM_JWT"
    CT="Content-Type: application/json"

    # ── Platform tenant baseline ─────────────────────────────────────────
    # Seed at least one agent in the platform tenant so the platform-admin
    # UI (and E2E tests that operate as platform admin) have data to render.
    PLATFORM_TENANT="X-Tenant-Id: platform"
    curl -sf -X POST "$API_BASE/api/v1/admin/users" -H "$CT" -H "$AUTH" -H "$PLATFORM_TENANT" \
        -d '{"userId":"platform-user-ops","email":"ops@platform.local","displayName":"Platform Ops","role":"Agent","password":"PlatformOps2026!"}' > /dev/null 2>&1 || true
    curl -sf -X POST "$API_BASE/api/v1/admin/agents" -H "$CT" -H "$AUTH" -H "$PLATFORM_TENANT" \
        -d '{"agentId":"platform-agent-ops","userId":"platform-user-ops","displayName":"Platform Ops","extension":"1001","sipPassword":"platform1001","skills":["support"]}' > /dev/null 2>&1 || true
    # Seed at least one queue + bot on platform tenant so platformAdmin E2E
    # listing tests render DataTable instead of EmptyState (fail loud on contract drift).
    curl -fsS -X POST "$API_BASE/api/v1/admin/queues" -H "$CT" -H "$AUTH" -H "$PLATFORM_TENANT" \
        -d '{"name":"Platform Ops","isActive":true}' > /dev/null
    curl -fsS -X POST "$API_BASE/api/v1/admin/bots" -H "$CT" -H "$AUTH" -H "$PLATFORM_TENANT" \
        -d '{"name":"Platform Bot","confidenceThreshold":0.7,"maxTurns":20,"isActive":true}' > /dev/null

    # ── Demo tenant seed (unchanged below) ───────────────────────────────
    TENANT="X-Tenant-Id: demo"

    # Create demo admin user
    curl -sf -X POST "$API_BASE/api/v1/admin/users" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"userId":"demo-user-admin","email":"admin@demo.local","displayName":"Demo Admin","role":"Admin","password":"DemoAdmin2026!"}' > /dev/null 2>&1 || true
    # Create demo supervisor
    curl -sf -X POST "$API_BASE/api/v1/admin/users" -H "$CT" -H "$AUTH" -H "$TENANT" \
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
        curl -sf -X POST "$API_BASE/api/v1/admin/users" -H "$CT" -H "$AUTH" -H "$TENANT" \
            -d "{\"userId\":\"$uid\",\"email\":\"$email\",\"displayName\":\"$name\",\"role\":\"Agent\",\"password\":\"DemoAgent2026!\"}" > /dev/null 2>&1 || true
        # Create agent
        curl -sf -X POST "$API_BASE/api/v1/admin/agents" -H "$CT" -H "$AUTH" -H "$TENANT" \
            -d "{\"agentId\":\"$aid\",\"userId\":\"$uid\",\"displayName\":\"$name\",\"extension\":\"$ext\",\"sipPassword\":\"$sippwd\",\"skills\":[\"$skill\"]}" > /dev/null 2>&1 || true
    done

    # Create queues (fail loud: contract changed to drop client-supplied queueId)
    curl -fsS -X POST "$API_BASE/api/v1/admin/queues" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"name":"Sales","isActive":true}' > /dev/null
    curl -fsS -X POST "$API_BASE/api/v1/admin/queues" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"name":"Support","isActive":true}' > /dev/null

    # Create demo bot (fail loud: bots are now multi-bot CRUD as of v1.6.x)
    curl -fsS -X POST "$API_BASE/api/v1/admin/bots" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"name":"Demo Bot","confidenceThreshold":0.7,"maxTurns":20,"isActive":true}' > /dev/null

    # Activate WebChat channel
    curl -sf -X PUT "$API_BASE/api/v1/admin/channels/webchat" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"isActive":true,"credentials":{}}' > /dev/null 2>&1 || true

    # Create teams
    curl -sf -X POST "$API_BASE/api/v1/admin/teams" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"name":"Sales Team"}' > /dev/null 2>&1 || true
    curl -sf -X POST "$API_BASE/api/v1/admin/teams" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"name":"Support Team"}' > /dev/null 2>&1 || true

    # Create contacts
    for contact in \
        'Juan|Perez|Acme Corp|enterprise|es' \
        'Laura|Gomez|TechStart|startup|es' \
        'Roberto|Silva|GlobalTrade|enterprise|pt' \
        'Carmen|Torres|MediSalud|healthcare|es' \
        'Miguel|Diaz|EduPlus|education|es'
    do
        IFS='|' read -r fn ln company segment lang <<< "$contact"
        curl -sf -X POST "$API_BASE/api/v1/contacts" -H "$CT" -H "$AUTH" -H "$TENANT" \
            -d "{\"firstName\":\"$fn\",\"lastName\":\"$ln\",\"company\":\"$company\",\"segment\":\"$segment\",\"preferredLanguage\":\"$lang\"}" > /dev/null 2>&1 || true
    done

    # Create canned responses
    curl -sf -X POST "$API_BASE/api/v1/admin/canned-responses" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"shortcut":"/saludo","title":"Saludo inicial","body":"Gracias por comunicarse con nosotros. En que puedo ayudarle?","category":"general"}' > /dev/null 2>&1 || true
    curl -sf -X POST "$API_BASE/api/v1/admin/canned-responses" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"shortcut":"/espera","title":"Solicitar espera","body":"Un momento mientras verifico la informacion. Podria esperar?","category":"general"}' > /dev/null 2>&1 || true
    curl -sf -X POST "$API_BASE/api/v1/admin/canned-responses" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"shortcut":"/transfer","title":"Transferencia","body":"Voy a transferirlo con un especialista. Un momento por favor.","category":"general"}' > /dev/null 2>&1 || true
    curl -sf -X POST "$API_BASE/api/v1/admin/canned-responses" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"shortcut":"/cierre","title":"Cierre","body":"Hay algo mas en que pueda ayudarle? Gracias por contactarnos.","category":"general"}' > /dev/null 2>&1 || true
    curl -sf -X POST "$API_BASE/api/v1/admin/canned-responses" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"shortcut":"/escalar","title":"Escalacion","body":"Voy a escalar su caso a un supervisor para una mejor solucion.","category":"support"}' > /dev/null 2>&1 || true

    # Create dispositions
    for disp in 'Resuelto|0' 'Venta completada|0' 'Seguimiento requerido|2' 'Sin respuesta|1' 'Numero equivocado|1' 'Spam|1'; do
        IFS='|' read -r name cat <<< "$disp"
        curl -sf -X POST "$API_BASE/api/v1/admin/dispositions" -H "$CT" -H "$AUTH" -H "$TENANT" \
            -d "{\"name\":\"$name\",\"category\":$cat}" > /dev/null 2>&1 || true
    done

    # Create scheduled report (valid types: agent_performance, queue_analytics, conversation_summary)
    curl -sf -X POST "$API_BASE/api/v1/admin/reports" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"name":"Daily Queue Summary","reportType":"queue_analytics","schedule":"0 8 * * *","recipients":"admin@demo.local","format":"pdf","isActive":true}' > /dev/null 2>&1 || true

    # Create webhook subscription (valid types: conversation.assigned, conversation.message, conversation.state_changed, agent.state_changed, campaign.*)
    curl -sf -X POST "$API_BASE/api/v1/webhooks/subscriptions" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"name":"Demo Events","endpointUrl":"https://webhook.example.com/demo","eventTypes":["conversation.assigned","conversation.message","agent.state_changed"]}' > /dev/null 2>&1 || true

    # Create survey (CSAT)
    curl -sf -X POST "$API_BASE/api/v1/admin/surveys" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"name":"Satisfaccion del cliente","type":"Csat","questions":[{"text":"Como calificaria el servicio recibido?","type":"Scale"}],"isActive":true}' > /dev/null 2>&1 || true

    # Create KB article
    curl -sf -X POST "$API_BASE/api/v1/admin/articles" -H "$CT" -H "$AUTH" -H "$TENANT" \
        -d '{"title":"Horarios de atencion","content":"Nuestro horario es de lunes a viernes 8:00-18:00. Sabados 9:00-13:00.","tags":["horarios","general"],"isPublished":true,"language":"es"}' > /dev/null 2>&1 || true

    echo "  OK (admin, supervisor, 6 agents, 2 queues, 1 bot, webchat, 2 teams, 5 contacts, 5 canned responses, 6 dispositions, 1 report, 1 webhook, 1 survey, 1 article)"
fi

# 9.5. Seed billing data via Management API (requires mgmt key, not JWT)
echo "[9.5/11] Configurando billing..."
if [ -n "$MGMT_KEY" ]; then
    MGMT_AUTH="Authorization: Bearer $MGMT_KEY"
    CT="Content-Type: application/json"

    # Rate card
    curl -sf -X POST "$API_BASE/api/v1/management/rate-cards?tenantId=demo" \
        -H "$CT" -H "$MGMT_AUTH" \
        -d '{"name":"Standard 2026","currency":"USD","effectiveFrom":"2026-01-01T00:00:00Z","isDefault":true,"rates":[{"usageType":"VoiceInbound","unitPrice":0.02,"unit":"Minute","includedQuantity":1000},{"usageType":"VoiceOutbound","unitPrice":0.03,"unit":"Minute","includedQuantity":500},{"usageType":"Message","unitPrice":0.005,"unit":"Message","includedQuantity":5000},{"usageType":"ActiveAgent","unitPrice":25.00,"unit":"Agent","includedQuantity":5}]}' > /dev/null 2>&1 || true

    # Quota
    curl -sf -X PUT "$API_BASE/api/v1/management/tenants/demo/quota" \
        -H "$CT" -H "$MGMT_AUTH" \
        -d '{"maxConcurrentChannels":20,"maxActiveCampaigns":5,"maxMonthlyVoiceMinutes":10000,"maxMonthlyMessages":50000,"maxStorageBytes":5368709120,"maxActiveAgents":10}' > /dev/null 2>&1 || true

    echo "  OK (1 rate card, quotas configured)"
else
    echo "  SKIP (no management key)"
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
LOGIN_RESULT=$(curl -sf -X POST "$API_BASE/api/v1/auth/login" \
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
echo "    Management API Key:          (run POST /api/v1/setup to generate)"
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
