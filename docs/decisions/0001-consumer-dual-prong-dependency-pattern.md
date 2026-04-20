# ADR-0001: Consumer dual-prong dependency pattern (SDK + Pro direct)

- **Status:** Proposed
- **Date:** 2026-04-20
- **Deciders:** Harold Reina
- **Related:**
  - PSD v2: `docs/specs/2026-04-19-product-strategy-v2.md` §1.2, §4
  - Pro CLAUDE.md (define strict ladder narrative)
  - SDK ADR-0026 (product identity)

## Context

Actualmente `Asterisk.Platform` consume el stack ecosystem con **dos prongs de dependencia:**

1. **Via Pro** (21 package references en 1.8.1): `Asterisk.Sdk.Pro.Dialer`, `.Analytics`, `.Cluster`, etc.
2. **Direct SDK** (2 references): `Asterisk.Sdk.Hosting`, `Asterisk.Sdk.Push`.

**Evidencia:**
- `src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj:64` — `<PackageReference Include="Asterisk.Sdk.Hosting" />`
- `src/Asterisk.Platform.Core/Asterisk.Platform.Core.csproj:13` — `<PackageReference Include="Asterisk.Sdk.Push" />`

**Esto contradice la narrativa "strict ladder"** en `Asterisk.Sdk.Pro/CLAUDE.md:7`:
> Dependency chain: SDK (MIT) → SDK.Pro → Platform ← Platform.Web (API-first)

La flecha implica que Platform consume Pro exclusivamente, y Pro lo que re-exporta de SDK es lo que Platform obtiene. La realidad es que Platform tiene un **two-pronged dependency**: una prong via Pro (features enterprise), otra prong directo a SDK (primitives de bajo nivel como Hosting y Push que no necesitan Pro wrapper).

**¿Es esto un problema?**

**Arquitectónicamente es válido.** Pattern análogo: ASP.NET Core apps consumen `Microsoft.EntityFrameworkCore` directo + `Microsoft.AspNetCore.*` directo, no via una capa wrapper. Si un feature de SDK no necesita Pro enrichment (tenancy, licensing, cluster), depender directo es cleaner que forzarlo via Pro re-export.

**Pero la documentación miente.** Claude.md de Pro dice strict ladder. Platform README no menciona dual-prong. Developers nuevos asumen Pro es único path. Cuando encuentran el `Asterisk.Sdk.Hosting` direct reference, quedan confundidos.

## Decision

**Platform ADOPTS dual-prong dependency pattern explícitamente + documenta la convención.**

### Pattern canónico

Platform puede depender directo de SDK packages cuando:
1. **El feature es primitive pura** que no requiere Pro enhancement (ej: `Asterisk.Sdk.Hosting` composition root, `Asterisk.Sdk.Push` bus base).
2. **Depender via Pro agregaría overhead sin valor** (Pro no re-exporta ni enriquece el primitive).
3. **El uso es single-tenant / no-multi-tenant** (si fuera multi-tenant, Pro tenant-aware wrapper es required).

Platform DEBE depender de Pro cuando:
1. **El feature requiere multi-tenant context** (todo concerning `ITenantContext`, licensing, cluster coordination).
2. **El feature requiere Pro orchestration** (Dialer, Analytics engine, CallAnalytics, AgentAssist, Routing, EventStore completo).
3. **Pro agrega enrichment valioso** sobre SDK primitive (ej: Pro.Push.SignalR hub sobre SDK Push).

### Dependencies actuales validados

| Platform package | Reference | Pattern | Justificación |
|---|---|---|---|
| `Asterisk.Platform.Api` | `Asterisk.Sdk.Hosting` (direct) | ✅ Valid direct | Composition root, single-tenant primitive |
| `Asterisk.Platform.Core` | `Asterisk.Sdk.Push` (direct) | ✅ Valid direct | Event bus base, tenant-scoping es Platform concern |
| `Asterisk.Platform.*` | `Asterisk.Sdk.Pro.*` (21 refs) | ✅ Valid via Pro | Todos cruzan multi-tenancy gate (ADR Pro-0004) |

### Nueva dependency rule

Cuando se agregue nueva dependency en Platform:
1. Evaluate: ¿el feature requiere Pro enrichment?
   - Sí → depend on `Asterisk.Sdk.Pro.*`.
   - No → OK to depend direct on `Asterisk.Sdk.*`.
2. Document: PR description explicit la prong elegida + razón.
3. Review: 2-pronged dependency count en Directory.Packages.props se tracked (hoy 2 direct SDK refs). Si crece significativamente, revisión de si Pro tier cubre correctamente los primitives.

### Documentation updates

1. **Platform README:** sección "Dependencies" explica dual-prong pattern.
2. **Pro CLAUDE.md línea 7:** actualizar narrativa de "strict ladder" a "Platform depende de Pro para multi-tenant features + directo de SDK para primitives puros".
3. **Platform CLAUDE.md:** linking a este ADR para reference.

## Consequences

**Positivas:**
- Narrativa alineada con realidad. Nuevos contributors no confundidos.
- Permite cleaner dependencies — no fuerza re-export innecesario via Pro.
- Pattern replicable: future additions follow la rule explícita.
- Reduce surface de Pro (no tiene que re-export cada SDK primitive que Platform use).

**Negativas:**
- 2-pronged dependency es más complejo de reasoning que strict ladder.
- Version coordination: Platform debe trackear compat matrix de SDK + Pro independientemente (aunque Pro pinea SDK, Platform puede pinear SDK direct a otra version si necesario).

**Mitigación:**
- Compat matrix publicada en cada release (PSD §5.3).
- Renovate cross-repo automation (PSD §9 Mes 2) alivia manual tracking.

## Alternatives considered

- **Strict ladder (remove direct SDK refs):** rechazado — forzaría Pro a re-export cada primitive SDK que Platform use. Overhead sin valor.
- **Strict ladder + Pro meta-package:** rechazado — misma overhead, plus complica publish.
- **Plat.Web consumes SDK direct too:** out of scope — .Web es frontend (TypeScript), no tiene .NET deps.
- **No documentation (status quo):** rechazado — la confusión persiste.

## References

- PSD §1.2 identity table, §4 layout
- Pro CLAUDE.md (will be updated per this ADR)
- Directory.Packages.props current references
- ASP.NET Core + EntityFramework Core dual-prong (historical analog)
