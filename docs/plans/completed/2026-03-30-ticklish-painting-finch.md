# Demo Architecture Fix — Separation of Application vs Demo Data

## Context

**El problema:** Program.cs contiene un bloque de seed que crea usuarios demo, queues, channels, tenants, API keys y sincroniza con Asterisk. Este bloque corre en TODOS los ambientes excepto "Testing" (incluido Production). Esto contamina un despliegue a producción con datos demo.

**El principio:** La aplicación debe arrancar limpia. Los datos demo solo existen en el ambiente demo. Program.cs solo debe contener lógica de aplicación (DI, middleware, endpoints). El setup wizard (`POST /api/setup`) es el mecanismo correcto para inicializar una instalación nueva.

---

## Clasificación de Entidades en el Dev Seed Actual

| Entidad | Clasificación | Destino Correcto |
|---------|--------------|-----------------|
| Platform tenant ("platform") | APP — pero vía setup wizard | `POST /api/setup` (ya existe) |
| Demo tenant ("demo") | DEMO | SQL seed script |
| Platform admin user | APP — pero vía setup wizard | `POST /api/setup` (ya existe) |
| Management API key | APP — pero vía setup wizard | `POST /api/setup` (ya existe) |
| Demo admin/supervisor/agents (8 users) | DEMO | SQL seed script |
| Demo API keys (8 keys) | DEMO | SQL seed script |
| Demo queues (sales/support) | DEMO | SQL seed script |
| Demo channel config (WebChat) | DEMO | SQL seed script |
| Demo tenant auth config | DEMO | SQL seed script |
| Asterisk Realtime sync | DEMO | SQL seed script |
| RBAC seeder (permissions + templates) | APP | Queda en Program.cs (solo con Postgres) |

---

## Plan

### Task 1: Program.cs — Eliminar dev seed, dejar solo RBAC

**File:** `src/Asterisk.Platform.Api/Program.cs`

El bloque completo de dev seed (desde `// ─── Dev seed:` hasta antes de `app.Run()`) se reemplaza por:

```csharp
// ─── RBAC seed: permissions, role templates (Postgres only) ──────────────────
if (!app.Environment.IsEnvironment("Testing"))
{
    var npgsqlDs = app.Services.GetService<NpgsqlDataSource>();
    if (npgsqlDs is not null)
    {
        try
        {
            await Asterisk.Platform.Storage.Postgres.Seeds.RbacSeederOrchestrator
                .SeedRbacAsync(npgsqlDs, CancellationToken.None);
            Console.WriteLine("RBAC seeder: permissions, templates, and role migration complete.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RBAC seeder skipped: {ex.Message}");
        }
    }
}

app.Run();
```

Se elimina TODO lo demás: tenants, users, agents, API keys, auth config, queues, channels, Realtime sync. Producción arranca limpia y usa `POST /api/setup` para inicializarse.

### Task 2: Demo SQL Seed — Crear `030_demo_platform_seed.sql`

**File:** `docker/demo/sql/030_demo_platform_seed.sql`

Este SQL se ejecuta DESPUÉS de que el API haya creado las tablas Pro (via EnsureSchemaAsync). Inserta directamente en Postgres toda la data demo que antes estaba en Program.cs.

Contenido: INSERT INTO las tablas de Platform (users, api_keys, agents, queue_configs, tenant_channel_configs, tenant_auth_configs) con los mismos datos del seed actual.

**Problema:** Las tablas Platform son creadas por migrations que corren al iniciar Postgres (001-007.sql en `/docker-entrypoint-initdb.d`). Pero las tablas Pro (dialer, analytics, realtime) son creadas por `EnsureSchemaAsync` al arrancar el API. El seed SQL necesita correr después del API.

**Solución:** El seed se ejecuta en `demo-reset.sh` paso [7/9] igual que los otros seeds, DESPUÉS de que el API esté healthy.

### Task 3: demo-reset.sh — Orquestar setup + seed

**File:** `docker/demo/demo-reset.sh`

Después de que el API esté healthy (paso 6), agregar:

```bash
# 7. Initialize platform via setup wizard
echo "[7/10] Inicializando plataforma..."
SETUP_RESPONSE=$(curl -sf -X POST http://localhost:5000/api/setup \
    -H "Content-Type: application/json" \
    -d '{
        "email": "platform@admin.local",
        "password": "PlatformAdmin2026!",
        "displayName": "Platform Admin",
        "platformName": "Asterisk Platform"
    }' 2>/dev/null)
echo "  Setup: $(echo $SETUP_RESPONSE | head -c 100)"

# 8. Load platform demo data
echo "[8/10] Cargando datos demo Platform..."
docker compose -f "$COMPOSE_FILE" exec -T postgres \
    psql -U platform -d platform -f /demo-sql/030_demo_platform_seed.sql -q

# 9. Load Asterisk seed data
echo "[9/10] Cargando datos seed Asterisk..."
docker compose -f "$COMPOSE_FILE" exec -T postgres \
    psql -U platform -d platform -f /demo-sql/010_demo_asterisk_seed.sql -q

# 10. Load historical data + warmup
echo "[10/10] Cargando datos historicos..."
docker compose -f "$COMPOSE_FILE" exec -T postgres \
    psql -U platform -d platform -f /demo-sql/020_demo_historical_data.sql -q
```

### Task 4: Fix SSE auth (aplicación, no demo)

**File:** `src/Asterisk.Platform.Api/Auth/AuthSchemeConfiguration.cs`

Esto SÍ es un fix de aplicación — SSE necesita JWT via query param en cualquier ambiente. Ya está implementado en el paso anterior, solo confirmar que está correcto.

### Task 5: Fix channel list endpoint (aplicación, no demo)

**File:** `src/Asterisk.Platform.Api/Endpoints/ChannelConfigEndpoints.cs`

Esto SÍ es un fix de aplicación — el frontend necesita `GET /api/admin/channels` en cualquier ambiente. Ya implementado.

### Task 6: Revertir cambios demo de Program.cs

Los cambios que hice anteriormente en Program.cs (platform tenant, demo tenant, platform admin, management key, demo queues, demo channel) deben ser revertidos. Program.cs vuelve a su estado original EXCEPTO:
- Se mantiene `if (!app.Environment.IsEnvironment("Testing"))` como guarda
- Se elimina todo el contenido del bloque de seed excepto RBAC

---

## Resultado Final

| Ambiente | Qué pasa al arrancar |
|----------|---------------------|
| **Production** | App limpia. Corre RBAC seeder si hay Postgres. Admin usa `POST /api/setup` para inicializar. |
| **Testing** | Nada. Factories proveen sus propios datos. |
| **Demo (docker)** | App limpia → demo-reset.sh llama `/api/setup` → SQL seed inserta demo data. |
| **Development** (dotnet run local) | App limpia. Developer usa `POST /api/setup` manualmente o un script local. |

---

## Verificación

1. `dotnet build Asterisk.Platform.slnx` — 0 errors, 0 warnings
2. `dotnet test Asterisk.Platform.slnx` — all tests pass (factories no dependen del seed)
3. `docker compose -f docker/demo/docker-compose.demo.yml up` + `demo-reset.sh` → demo funcional
4. Sin demo-reset.sh, el API arranca limpio (solo health check responde)
5. `POST /api/setup` funciona en instancia limpia

## Files Changed

| File | Change |
|------|--------|
| `src/Asterisk.Platform.Api/Program.cs` | Eliminar dev seed completo, dejar solo RBAC seeder |
| `src/Asterisk.Platform.Api/Auth/AuthSchemeConfiguration.cs` | JWT query param para SSE (ya hecho) |
| `src/Asterisk.Platform.Api/Endpoints/ChannelConfigEndpoints.cs` | Lista de channels (ya hecho) |
| `docker/demo/sql/030_demo_platform_seed.sql` | NUEVO — toda la data demo en SQL |
| `docker/demo/demo-reset.sh` | Orquestar setup wizard + SQL seeds |
