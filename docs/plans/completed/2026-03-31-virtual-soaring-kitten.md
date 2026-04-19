# Plan: Demo Funcional como Producto Final

## Context

El demo Docker (8 servicios) nunca se ha desplegado end-to-end exitosamente. La pregunta es: arreglar el demo existente (InMemory, datos efimeros) vs. hacer un demo que funcione como el producto real (Postgres, datos persistentes).

**Hallazgo clave:** `AddPostgresStorage()` ya existe con 31 stores implementados en Dapper/Npgsql, la migración SQL con 34 tablas está lista (`001_InitialSchema.sql`), y hay 5 tests unitarios. El cambio en Program.cs es de ~5 lineas. **No hay que implementar nada nuevo.**

## Diagnosis: Por qué el demo nunca funcionó

1. **Conflicto Storage dual:** Program.cs siempre registra `AddInMemoryStorage()`. Los SQL seeds (030_demo_platform_seed.sql) insertan en Postgres pero la API lee de InMemory — datos invisibles.
2. **demo-reset.sh duplica trabajo:** Step 9 crea users/agents via API (→ InMemory), Step 10 inserta lo mismo en SQL (→ Postgres que nadie lee). Conflicto silencioso.
3. **Dockerfile.asterisk:** Descarga codec_opus de Digium — URLs potencialmente rotas.
4. **Prometheus:** Solo se scrapes a si mismo, no al API.
5. **Nunca se ha probado end-to-end:** Nadie ha corrido demo-reset.sh completo.

## Recommendation: Option B — Demo como Producto Real

**Esfuerzo real: 2-3 dias** (no semanas, porque los 31 Postgres stores ya existen).

### Pros vs Option A (InMemory)

| Aspecto | Option A (InMemory) | Option B (Postgres) |
|---------|---------------------|---------------------|
| Persistencia | Pierde datos al reiniciar | Datos persisten |
| Post-restart | Requiere re-run demo-reset.sh | Solo `docker compose up` |
| Representatividad | No refleja produccion | Identico a produccion |
| SQL seeds | Inutiles (API no los lee) | Funcionan correctamente |
| Testing value | Bajo | Valida 31 Postgres stores en integracion |
| Esfuerzo | 1-2 dias | 2-3 dias (+1 dia) |

### Riesgos Option B

- Los 31 Postgres stores nunca se han probado en integracion real — pueden tener bugs en queries Dapper
- NpgsqlDataSource double-registration (AddPostgresStorage + Pro packages)
- El setup wizard y tenant creation requieren runtime API (no se pueden mover a SQL)

## Implementation Steps

### Phase 1: Program.cs Storage Switch
**File:** `src/Asterisk.Platform.Api/Program.cs:68-70`

Cambiar:
```csharp
builder.Services.AddInMemoryStorage();
```
Por:
```csharp
var coreConnectionString = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrEmpty(coreConnectionString))
    builder.Services.AddPostgresStorage(coreConnectionString);
else
    builder.Services.AddInMemoryStorage();
```

Verificar que no haya double-registration de NpgsqlDataSource con los Pro packages (lineas 101-167).

### Phase 2: Fix Dockerfile.asterisk
**File:** `docker/Dockerfile.asterisk`

- Verificar si URLs de Digium codec_opus siguen activas
- Si no: usar codec opus integrado en Asterisk 22, o buscar mirror alternativo
- Mismo fix en `docker/demo/Dockerfile.demo-pstn`

### Phase 3: Simplify demo-reset.sh
**File:** `docker/demo/demo-reset.sh`

- Step 7 (setup wizard) y Step 8 (create tenant): MANTENER via API — requieren runtime
- Step 9 (seed users/agents/queues via API): SIMPLIFICAR — solo crear lo que NO está en SQL seeds
- Step 10 (SQL seeds): 030_demo_platform_seed.sql ahora SI es visible para la API
- Resolver: evitar duplicacion Step 9 vs SQL seed. Opcion preferida: **API calls para todo** (step 9) y remover 030_demo_platform_seed.sql, porque los API calls validan business logic

### Phase 4: Fix Prometheus
**File:** `docker/demo/prometheus/prometheus.yml`

Agregar scrape target: `platform-api:5000/metrics`

### Phase 5: Validate Grafana Dashboard
**File:** `docker/demo/grafana/provisioning/dashboards/contact-center.json`

Verificar que las queries SQL referencien las tablas correctas del schema Pro (completed_sessions, interval_snapshots).

### Phase 6: End-to-End Test
1. Run `demo-reset.sh`
2. Verificar: login funciona, datos persisten tras restart
3. Verificar: Grafana muestra datos historicos
4. Verificar: WebRTC endpoints registran en Asterisk
5. Verificar: PSTN emulator conecta

## Critical Files

| File | Change |
|------|--------|
| `src/Asterisk.Platform.Api/Program.cs` | Storage conditional switch |
| `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs` | Verify NpgsqlDataSource handling |
| `docker/Dockerfile.asterisk` | Fix codec download |
| `docker/demo/demo-reset.sh` | Simplify seeding, remove duplication |
| `docker/demo/prometheus/prometheus.yml` | Add API scrape target |
| `docker/demo/sql/030_demo_platform_seed.sql` | Evaluate: keep or remove |

## Verification

```sh
cd docker/demo
./demo-reset.sh

# Test persistence:
docker compose down
docker compose up -d
# Wait for health checks
curl http://localhost:5000/api/auth/login -d '{"tenantId":"demo","email":"admin@demo.local","password":"DemoAdmin2026!"}'
# Should return JWT without re-seeding

# Test Grafana:
# http://localhost:3000 should show historical data

# Test Asterisk:
docker exec -it demo-asterisk-1 asterisk -rx "pjsip show endpoints"
# Should show 6 WebRTC endpoints + pstn-trunk
```
