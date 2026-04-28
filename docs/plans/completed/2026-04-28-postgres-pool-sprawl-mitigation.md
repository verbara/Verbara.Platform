# Postgres Pool Sprawl Mitigation — Execution Plan

> **For agentic workers:** Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cerrar el bug arquitectónico expuesto por R5.5 Phase C-L `presence` sweep — Platform.Api con todas las features Pro habilitadas crea **~14 `NpgsqlDataSource` singletons** sobre el mismo connection string, cada uno con `Maximum Pool Size=100` default Npgsql → potencial demand 1 400 conns/instance contra `max_connections=100` Postgres alpine default → 13 % 5xx error rate observado @ VU=100 concurrent reads.

**Why now:** R5.5 production validation no puede cerrar honestamente con este bug latente. Cualquier customer SMB que despliegue `docker-compose.full.yml` o `production.yml` con todas las Pro features hits the same wall en su primer pico de 100 usuarios concurrentes. Esto compromete el SMB tier promised en `capacity-planning.md`.

**Why not "just bump max_connections=400":** No fixea el sprawl arquitectónico — solo lo enmascara. Pro 1.16.0-pro shared-DataSource overload es la fix raíz pero cross-repo y bloquearía R5.5 ship. Estrategia bifásica:

- **Fase 1 (este plan):** smart pool-size defaults en Platform.Api + dedicated `docker-compose.smb.yml` con tuning Postgres + ADR-0015 documentando estrategia + re-medición SMB tier knee. **Self-contained en Platform repo.**
- **Fase 2 (Pro 1.16.0-pro, plan separado):** `Use*Storage(IServiceCollection, NpgsqlDataSource)` overload across all Pro storage packages. Platform.Api pasa una sola instancia compartida → 1 pool real. **Cross-repo, NO bloquea R5.5.**

**Tech stack:** .NET 10 Native AOT · Npgsql 9.x · Dapper · Postgres 18-alpine · Docker Compose v2 · NBomber 6.1.0 · TreatWarningsAsErrors=true.

**Spec source:** Conversational analysis 2026-04-28 + R5.5 Phase C-L sweep findings + ADR-0014 §"Postgres pool tuning" pre-existing baseline.

---

## File Structure Overview

### NEW

```
docs/plans/active/
  └── 2026-04-28-postgres-pool-sprawl-mitigation.md      — THIS FILE

docs/decisions/
  ├── 0015-npgsql-datasource-sharing-strategy.md         — NEW (Proposed → Accepted en Phase D.1)
  └── 0014-auth-horizontal-scaling-baseline.md           — AMENDMENT (corrige "1 pool per replica")

docker/
  └── docker-compose.smb.yml                             — NEW (SMB tier representativo del producto)

src/Asterisk.Platform.Api/Services/
  └── ConnectionStringDefaults.cs                        — NEW (smart Maximum Pool Size default)

tests/Asterisk.Platform.Api.Tests/Services/
  └── ConnectionStringDefaultsTests.cs                   — NEW (auto-tune unit tests)

docs/research/archived/Pro-1.16.0-pro-shared-datasource-skeleton.md
                                                         — NEW (Fase 2 plan skeleton)
```

### MODIFIED

```
docker/
  ├── docker-compose.full.yml                            — bump max_connections=200 (dev stack stability)
  └── docker-compose.production.yml                      — apply SMB tier tuning (production-ready defaults)

src/Asterisk.Platform.Api/
  └── Program.cs                                         — invoke ConnectionStringDefaults pre Pro registration

docs/operations/
  ├── capacity-planning.md                               — SMB tier section: explicit pool-size requirement
  └── load-test-baseline.md                              — Phase C-L section: 4 datasets + sprawl diagnosis

CHANGELOG.md                                             — entry para v1.14.5
Directory.Build.props                                    — bump 1.14.4 → 1.14.5
```

---

## Phase A · Foundation (batch, low risk)

**Premise:** Compose files + skeleton doc + capacity baseline. No code change. Cero riesgo de regresión.

### Task A.1: Create skeleton ADR-0015

- [ ] **Step 1:** Crear `docs/decisions/0015-npgsql-datasource-sharing-strategy.md` con frontmatter:
  - **Status:** Proposed
  - **Context:** Inventario de 14 DataSources cross-Pro (data point R5.5 Phase C-L)
  - **Decision:** Estrategia bifásica (smart defaults + Pro 1.16.0-pro shared overload)
  - **Consequences:** Trade-offs explícitos + medidas pendientes (Phase D.1 promueve a Accepted con measured data)

```bash
git add docs/decisions/0015-npgsql-datasource-sharing-strategy.md
git commit -m "docs(adr): add ADR-0015 NpgsqlDataSource sharing strategy (Proposed)"
```

### Task A.2: Create docker-compose.smb.yml

- [ ] **Step 1:** Crear `docker/docker-compose.smb.yml` representando producto SMB tier real:
  - Hereda servicios base de full.yml vía `extends` o composición
  - Postgres `command: ["-c", "max_connections=200", "-c", "shared_buffers=512MB", "-c", "effective_cache_size=2GB"]`
  - `ConnectionStrings__Postgres: "...Maximum Pool Size=10;Minimum Pool Size=2;Connection Idle Lifetime=300;Pooling=true"`
  - Comentario header explicando que este es el "SMB tier production-ready" stack vs full.yml dev/loadtest stack
  - Explicit reference a ADR-0015

- [ ] **Step 2:** Smoke test: `docker compose -f docker/docker-compose.smb.yml up -d --wait` → todos servicios healthy

```bash
git add docker/docker-compose.smb.yml
git commit -m "feat(docker): add docker-compose.smb.yml — SMB tier production-ready stack (ADR-0015)"
```

### Task A.3: Patch docker-compose.full.yml + production.yml

- [ ] **Step 1:** Patch `docker/docker-compose.full.yml` postgres service con `max_connections=200` (suficiente headroom para sweeps de loadtest sin tunear lo demás — sigue siendo dev stack)

- [ ] **Step 2:** Patch `docker/docker-compose.production.yml` con la **misma config completa de smb.yml** (production.yml ES el SMB tier production-ready desde la perspectiva del operador)

- [ ] **Step 3:** Header comments en cada compose explicando claramente:
  - `full.yml` = dev/loadtest stack
  - `smb.yml` = SMB tier production-ready stack (1 platform-api instance)
  - `production.yml` = SMB tier production-ready stack (alias de smb.yml para compatibilidad operator)
  - `scale.yml` = Enterprise tier 4-replica stack

```bash
git add docker/docker-compose.full.yml docker/docker-compose.production.yml
git commit -m "fix(docker): bump max_connections=200 in full.yml + apply SMB tuning to production.yml"
```

### Phase A gate

- [ ] All 3 commits clean (Conventional Commits)
- [ ] Build `dotnet build Asterisk.Platform.slnx -c Release` 0 W / 0 E
- [ ] `docker-compose.smb.yml` smoke OK

---

## Phase B · Critical components (individual focused subagents)

**Premise:** Code change con unit tests. Riesgo medio porque toca composition root. Sub-tasks aisladas + verification antes de continuar.

### Task B.1: Implement ConnectionStringDefaults

- [ ] **Step 1:** Crear `src/Asterisk.Platform.Api/Services/ConnectionStringDefaults.cs`:

```csharp
namespace Asterisk.Platform.Api.Services;

/// <summary>
/// ADR-0015 Phase 1 mitigation: applies sane <c>Maximum Pool Size</c> +
/// <c>Minimum Pool Size</c> + <c>Connection Idle Lifetime</c> defaults to
/// connection strings if the operator didn't specify them. Each
/// <see cref="Npgsql.NpgsqlDataSource"/> across Platform + Pro packages
/// inherits the parsed connection-string keywords, so this gives a single
/// place to apply the SMB-tier ceiling.
/// </summary>
internal static class ConnectionStringDefaults
{
    /// <summary>
    /// Default per-data-source pool size when the operator did not specify one.
    /// 14 known data sources × 10 = 140 conn demand ceiling, comfortable under
    /// <c>max_connections=200</c> Postgres tuning shipped in smb.yml.
    /// </summary>
    public const int DefaultMaximumPoolSize = 10;
    public const int DefaultMinimumPoolSize = 2;
    public const int DefaultConnectionIdleLifetimeSeconds = 300;

    /// <summary>
    /// Returns <paramref name="connectionString"/> with pool-sizing defaults
    /// applied if missing. Operator-specified values are preserved verbatim.
    /// </summary>
    public static string ApplyPoolDefaults(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString ?? "";
        }

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);

        if (!builder.ContainsKey("Maximum Pool Size") &&
            !builder.ContainsKey("MaxPoolSize"))
        {
            builder.MaxPoolSize = DefaultMaximumPoolSize;
        }
        if (!builder.ContainsKey("Minimum Pool Size") &&
            !builder.ContainsKey("MinPoolSize"))
        {
            builder.MinPoolSize = DefaultMinimumPoolSize;
        }
        if (!builder.ContainsKey("Connection Idle Lifetime") &&
            !builder.ContainsKey("ConnectionIdleLifetime"))
        {
            builder.ConnectionIdleLifetime = DefaultConnectionIdleLifetimeSeconds;
        }

        return builder.ToString();
    }
}
```

- [ ] **Step 2:** Verify build 0 W / 0 E.

### Task B.2: Wire ConnectionStringDefaults in Program.cs

- [ ] **Step 1:** En `src/Asterisk.Platform.Api/Program.cs`, **antes** de la sección `─── Storage ────`, intercept `coreConnectionString` y todos los `*ConnectionString` derivados:

```csharp
// ─── ADR-0015 Phase 1: SMB-tier pool sizing defaults ──────────────────────
// Each Pro storage package creates its own NpgsqlDataSource (~14 total when
// all features active). Without explicit operator override, Npgsql defaults
// each to Maximum Pool Size=100 → potential 1 400-conn demand per platform-
// api instance. ConnectionStringDefaults applies sane SMB-tier ceiling
// (10 conns/pool) so total demand stays under max_connections=200.
// Pro 1.16.0-pro shared-DataSource overload (ADR-0015 Phase 2) eliminates
// the sprawl entirely.
var coreConnectionString = ConnectionStringDefaults.ApplyPoolDefaults(
    builder.Configuration.GetConnectionString("Postgres"));
```

- [ ] **Step 2:** Misma transformación a `dialerConnectionString`, `clusterConn`, `realtimeConn`, `analyticsConnectionString`, `liveAnalyticsConnectionString` antes de pasar a Pro `Use*Storage` calls.

- [ ] **Step 3:** Verify build 0 W / 0 E.

### Task B.3: Unit tests

- [ ] **Step 1:** Crear `tests/Asterisk.Platform.Api.Tests/Services/ConnectionStringDefaultsTests.cs`:

```csharp
public class ConnectionStringDefaultsTests
{
    [Fact]
    public void ApplyPoolDefaults_ShouldApplyDefaults_WhenOperatorDidNotSpecify()
    {
        var input = "Host=postgres;Database=platform;Username=u;Password=p";
        var result = ConnectionStringDefaults.ApplyPoolDefaults(input);

        var parsed = new NpgsqlConnectionStringBuilder(result);
        parsed.MaxPoolSize.Should().Be(ConnectionStringDefaults.DefaultMaximumPoolSize);
        parsed.MinPoolSize.Should().Be(ConnectionStringDefaults.DefaultMinimumPoolSize);
        parsed.ConnectionIdleLifetime.Should().Be(ConnectionStringDefaults.DefaultConnectionIdleLifetimeSeconds);
    }

    [Fact]
    public void ApplyPoolDefaults_ShouldPreserveOperatorOverride_WhenMaxPoolSizeSpecified()
    {
        var input = "Host=postgres;Database=platform;Username=u;Password=p;Maximum Pool Size=50";
        var result = ConnectionStringDefaults.ApplyPoolDefaults(input);

        var parsed = new NpgsqlConnectionStringBuilder(result);
        parsed.MaxPoolSize.Should().Be(50); // operator wins
    }

    [Fact]
    public void ApplyPoolDefaults_ShouldHandleNull_WithoutCrash() { ... }

    [Fact]
    public void ApplyPoolDefaults_ShouldHandleEmpty_WithoutCrash() { ... }

    [Fact]
    public void ApplyPoolDefaults_ShouldPartialOverride_PreserveOnlySpecified()
    {
        var input = "Host=postgres;Maximum Pool Size=20"; // operator set max, not min/idle
        var result = ConnectionStringDefaults.ApplyPoolDefaults(input);

        var parsed = new NpgsqlConnectionStringBuilder(result);
        parsed.MaxPoolSize.Should().Be(20); // operator
        parsed.MinPoolSize.Should().Be(ConnectionStringDefaults.DefaultMinimumPoolSize); // default
    }
}
```

- [ ] **Step 2:** Run tests: `dotnet test tests/Asterisk.Platform.Api.Tests/ -c Release` → 877+ tests pass (5 new).

```bash
git add src/Asterisk.Platform.Api/Services/ConnectionStringDefaults.cs \
        src/Asterisk.Platform.Api/Program.cs \
        tests/Asterisk.Platform.Api.Tests/Services/ConnectionStringDefaultsTests.cs
git commit -m "feat(api): SMB-tier pool sizing defaults — closes ADR-0015 Phase 1 sprawl mitigation"
```

### Phase B gate

- [ ] Build 0 W / 0 E full solution
- [ ] All Api.Tests pass (877+ baseline + 5 new = 882+)
- [ ] No new vulnerable packages

---

## Phase C · Measurement (sequential, validated baseline)

**Premise:** Re-medir el knee SMB tier con configuración correcta. El dataset reemplaza los runs anteriores de Phase C-L que reflejaban el bug, no el producto.

### Task C.1: SMB tier baseline (smb.yml)

- [ ] **Step 1:** `docker compose -f docker/docker-compose.smb.yml down -v` (clean state) + `up -d --wait`
- [ ] **Step 2:** `./scripts/seed-staging.sh` para regenerar tenants + tokens
- [ ] **Step 3:** `./scripts/scenario-sweep.sh all-reads` con ladders default
- [ ] **Step 4:** Capturar pg_stat_activity active_conns peak durante presence VU=100 (debe estar bajo 150 con tuning)
- [ ] **Step 5:** Capturar Grafana screenshots (HTTP latency p50/p95/p99 + Postgres pool usage + Postgres rejected conns counter)

### Task C.2: SMB tier knee identification

- [ ] **Step 1:** Si presence VU=100 ya no satura (esperado con max_connections=200), extender ladder a 250/500/1000/1500
- [ ] **Step 2:** Documentar el real knee:
  - **Esperado:** presence VU = N donde p99 OK > 250 ms o error rate > 5 % por 1 min sostenido
  - **Probable rango:** N ∈ [500, 2000] dado pool 10 × 14 sources = 140 demand ceiling

### Task C.3: Enterprise tier baseline (scale.yml)

- [ ] **Step 1:** `docker compose -f docker/docker-compose.scale.yml up -d --wait` + seed
- [ ] **Step 2:** Run presence sweep VU=100/250/500/1000/1500/2500
- [ ] **Step 3:** Note: scale.yml también tiene el sprawl bug (4 replicas × 14 pools × 50 size = 2 800 demand vs max_connections=220) → con Phase 1 fix Platform.Api ahora aplica MaxPoolSize=10 → 4 × 14 × 10 = 560 demand vs 220 server cap. Aún excede. **Documentar como hallazgo:** scale.yml necesita amendment a `max_connections=600` o esperar Pro 1.16.0-pro

```bash
git add tests/Asterisk.Platform.LoadTests/load-test-reports/2026-04-28-smb-tier/
git commit -m "test(loadtests): R5.5 C-L SMB tier baseline post-pool-sprawl-fix (ADR-0015 Phase 1)"
```

### Phase C gate

- [ ] SMB tier presence VU>100 sin Postgres rejection
- [ ] Real SMB tier knee documentado con measured numbers
- [ ] Enterprise tier sprawl impact cuantificado (input para Pro 1.16.0-pro plan)

---

## Phase D · Documentation (batch)

**Premise:** Documentar el viaje completo. Sin esto el fix queda "tribal knowledge".

### Task D.1: Promote ADR-0015 to Accepted

- [ ] **Step 1:** Update `docs/decisions/0015-npgsql-datasource-sharing-strategy.md`:
  - Status: Proposed → **Accepted** (date: 2026-04-28)
  - Context section: enriquecer con measured data de Phase C
  - Decision section: confirma Phase 1 shipped (v1.14.5), Phase 2 plan-skeleton archivado
  - Consequences: trade-offs reales (no especulativos)
  - References: Phase C reports + load-test-baseline.md sección Phase C-L

### Task D.2: ADR-0014 amendment

- [ ] **Step 1:** Append "Update 2026-04-28 (R5.5 Phase C-L)" section a `docs/decisions/0014-auth-horizontal-scaling-baseline.md`:
  - Corrects "1 pool per replica" assumption — la realidad es ~14 pools/replica
  - Updates 4-replica connection demand math (ahora 4 × 14 × 10 = 560 con Phase 1 fix)
  - Cross-reference a ADR-0015 Phase 2 como path para resolver

### Task D.3: capacity-planning.md SMB tier section

- [ ] **Step 1:** Update `docs/operations/capacity-planning.md` line 107 + alrededores:
  - Tier "Small" tuning row: agrega `Maximum Pool Size=10 per data source` requirement
  - Nueva sección "SMB tier production stack" referenciando smb.yml
  - Footnote explicando ADR-0015 Phase 1 + 2

### Task D.4: load-test-baseline.md Phase C-L section

- [ ] **Step 1:** Append "## Phase C-L stress sweep — SMB tier post-pool-sprawl-fix (2026-04-28)" a `docs/operations/load-test-baseline.md`:
  - 4 datasets: queues, livequeue (NotFound by design), agentassist, presence
  - Diagnosis del sprawl (NpgsqlDataSource.Create() ×14)
  - Reference a ADR-0015
  - Real measured SMB tier knee
  - Honest trade-off documentation

### Task D.5: CHANGELOG + version bump

- [ ] **Step 1:** Add `[1.14.5] — 2026-04-28 — "ADR-0015 Phase 1 — Postgres pool sprawl mitigation"` entry a `CHANGELOG.md`
- [ ] **Step 2:** Bump `Directory.Build.props` PackageVersion 1.14.4 → 1.14.5
- [ ] **Step 3:** Mismo bump en 3 csproj `<Version>` files (Api, Mail, Renderer)

```bash
git add docs/decisions/0015-npgsql-datasource-sharing-strategy.md \
        docs/decisions/0014-auth-horizontal-scaling-baseline.md \
        docs/operations/capacity-planning.md \
        docs/operations/load-test-baseline.md \
        CHANGELOG.md \
        Directory.Build.props \
        src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj \
        src/Asterisk.Platform.Mail/Asterisk.Platform.Mail.csproj \
        src/Asterisk.Platform.Renderer/Asterisk.Platform.Renderer.csproj
git commit -m "docs(release): ADR-0015 Accepted + capacity-planning SMB tier + v1.14.5 release notes"
git tag v1.14.5
```

### Phase D gate

- [ ] All docs cross-reference ADR-0015 + measured data
- [ ] CHANGELOG entry honest about Phase 1 vs Phase 2 split
- [ ] No marketing-grade inflation of claims

---

## Phase E · Pro 1.16.0-pro plan-skeleton (batch)

**Premise:** Captura el architectural fix como follow-up plan en Pro repo. NO scope code yet.

### Task E.1: Plan skeleton

- [ ] **Step 1:** Crear `docs/research/archived/Pro-1.16.0-pro-shared-datasource-skeleton.md`:
  - Goal: Pro packages exponen `Use*Storage(IServiceCollection, NpgsqlDataSource)` overload
  - Inventario: 13 sites en Pro repo donde `NpgsqlDataSource.Create(connectionString)` se llama hoy (audit data ya recolectado)
  - Plan ejecución bifásica: (a) overload aditivo binary-compatible, (b) Platform.Api adopta y mide
  - Acceptance criteria: 1 single NpgsqlDataSource cuando Platform.Api comparte conn string
  - Cross-repo coordination: Pro repo plans/active al ship time

```bash
git add docs/research/archived/Pro-1.16.0-pro-shared-datasource-skeleton.md
git commit -m "docs(research): archive Pro 1.16.0-pro shared-DataSource skeleton (ADR-0015 Phase 2)"
```

### Task E.2: Memory update

- [ ] **Step 1:** Crear `~/.claude/projects/-media-Data-Source-IPcom-Asterisk-Platform/memory/project_v1145_pool_sprawl_fix.md` con summary de v1.14.5 ship
- [ ] **Step 2:** Update `MEMORY.md` con pointer al nuevo topic file
- [ ] **Step 3:** Update `project_r55_validation_active.md` con cross-link al desvío Phase C-L → v1.14.5 → resume Phase C-L sweep secuencial real

### Phase E gate

- [ ] Plan-skeleton archivado, no leak de scope
- [ ] Memory actualizada para próxima conversación

---

## Sign-off criteria

- [ ] **Phase A** ✅ A.1-A.3 complete, build clean, smb.yml smoke OK
- [ ] **Phase B** ✅ ConnectionStringDefaults shipped + 5 new unit tests pass
- [ ] **Phase C** ✅ SMB tier knee measured + Enterprise tier impact quantified
- [ ] **Phase D** ✅ ADR-0015 Accepted + capacity-planning + load-test-baseline + CHANGELOG + tag v1.14.5
- [ ] **Phase E** ✅ Pro 1.16.0-pro plan-skeleton archived + memory updated
- [ ] **R5.5 resume** ✅ Phase C-L sweep secuencial real reanuda con SMB tier baseline correcto

## Estimated scope

| Phase | Risk | Effort | Dependencies |
|---|---|---|---|
| A | Low | 30-45 min | None |
| B | Medium | 45-60 min | A complete |
| C | Low | 60-90 min | B complete + Docker |
| D | Low | 30-45 min | C measured data |
| E | Low | 15-30 min | D complete |
| **Total** | — | **3-4.5 horas** | sequential phases |

## Out of scope

- **PgBouncer integration** — rejected en ADR-0014 (rompe Pro.Push LISTEN/NOTIFY)
- **Connection-leak audit** — pg_stat shows 6 idle conns post-sweep, no leak evidence
- **Pro 1.16.0-pro implementation** — captured as Phase E skeleton, deferred to Pro repo cycle
- **K8s migration of pool tuning** — scale.yml gets noted in Phase C.3, full fix awaits Pro 1.16.0-pro

## Cross-repo coordination

- Pro repo: NO change required for this plan (Fase 2 lives there, separate plan)
- Web repo: NO change (cosmetic-track)
- SDK repo: NO change

## References

- ADR-0014 — auth-horizontal-scaling-baseline (current "1 pool per replica" assumption corrected)
- ADR-0015 — npgsql-datasource-sharing-strategy (NEW)
- R5.5 Phase C-L sweep findings — `tests/Asterisk.Platform.LoadTests/load-test-reports/`
- v1.14.2 CHANGELOG entry "Postgres pool sizing for multi-replica"
- Pro repo audit: 13 `NpgsqlDataSource.Create(connectionString)` sites
