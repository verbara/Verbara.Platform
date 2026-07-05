# Plan: Documentación viva — manuales generados desde tests E2E (Playwright + híbrido)

## Contexto

**Por qué este plan ahora.** Tras el cierre de R5.5 (PRR firmada 2026-05-26) y el pivot estratégico 2026-05-25 ("no cloud hasta primer cliente pagando"), el track primario de Verbara.Platform es **SMB Docker product polish** — el camino para conseguir el primer cliente real. Hoy los manuales SMB se escriben a mano (12 archivos en [`docs/manuales/smb/`](../../manuales/smb/)) y cada release requiere re-sync manual (PR #37 lo hizo para v2.5.4: 28 referencias de versión + 12 comandos cosign actualizados a mano). El producto se mueve rápido (22 días v2.0 → v2.5.4); el costo recurrente de mantener docs a mano es deuda que crece con cada release.

**Qué se logra.** Una infraestructura de **documentación viva**: los manuales se regeneran automáticamente desde tests E2E reales contra el demo stack. Cada test cumple dos funciones simultáneas — valida que el producto funciona y produce el manual paso a paso. Si la UI cambia y el test falla, el operador sabe que hay un problema antes de que el cliente lo encuentre.

**Audiencia primaria.** Operadores SMB que instalan Verbara y administradores de tenant que configuran. Audiencias secundarias (agente, supervisor, QA analyst, platform admin, partner admin) se cubren en fases posteriores.

**Enfoque elegido — Híbrido.** Guion pedagógico escrito a mano por persona/journey (template `.md.tpl` con placeholders `{{step:NN}}`) + test E2E que emite screenshots etiquetados por step-ID + renderer que ensambla template + assets → `.md` final. Esto desacopla *qué se enseña* (narrativa humana, cambia poco) de *cómo se ve* (captura automática, cambia con cada UI tweak). Alternativas descartadas: test=manual completo (cualquier cambio de UI rompe el manual entero), separación total (= status quo, deuda recurrente), Scribe.how/SaaS propietario (no aplica al criterio "producto final sin atajos"). El análisis detrás está en el turn del Plan agent en esta misma conversación.

**Stack elegido.** Playwright (74 specs existentes, 0 referencias a Selenium — confirmado por exploración) + Allure reporter (capa estable sobre `trace.zip` interno) + plantillas `.md.tpl` híbridas + MkDocs Material (sitio HTML) + `Verbara.Platform.Renderer` extendido (PDF via QuestPDF, ya AOT-compatible en stack).

## Decisión arquitectónica

```
tests/manuales/
├── personas/
│   ├── smb-owner/
│   │   ├── 01-day1-setup-and-webchat.md.tpl   ← guion pedagógico (humano)
│   │   ├── 01-day1-setup-and-webchat.spec.ts  ← test E2E + screenshots etiquetados
│   │   ├── 02-day2-email-channel.{md.tpl,spec.ts}
│   │   └── ...
│   ├── tenant-admin/
│   ├── supervisor/
│   ├── agent/
│   ├── qa-analyst/
│   ├── platform-admin/
│   └── partner-admin/
├── manual-renderer/
│   ├── render.ts           ← lee Allure JSON + .md.tpl → .md final
│   ├── allure-adapter.ts   ← extrae steps + screenshots de Allure
│   └── template-engine.ts  ← reemplaza {{step:NN}} con assets
└── playwright.docs.config.ts  ← project "manuales": video on, trace on, screenshot per-step
```

**Output structure** (separado de manuales escritos a mano para evitar drift confuso):

```
docs/manuales/auto/v2.5.4/es/
├── smb-owner/
│   ├── 01-day1-setup-and-webchat.md       ← auto-generado
│   ├── 01-day1-setup-and-webchat/         ← screenshots embebidos
│   │   ├── step-01.png
│   │   └── ...
│   └── ...
└── tenant-admin/...
```

Mantenemos `docs/manuales/smb/` actual (escrito a mano) intacto durante las primeras fases para no romper el flujo de operadores actuales. Migración total a `auto/` ocurre cuando paridad de calidad esté validada por revisión humana (Fase 3).

## Fases (criterios de salida claros)

### Fase 0 — Scaffolding (~3 días)

Objetivo: probar el pipeline end-to-end con el manual más trivial posible.

- Crear `tests/manuales/` directory en `/media/Data/Source/Verbara/Verbara.Platform.Web/`
- `playwright.docs.config.ts`: project `manuales` con `video: 'on'`, `trace: 'on'`, `screenshot: 'on'`, retención de artifacts garantizada
- Instalar `allure-playwright` reporter (npm devDep)
- Primer template `personas/smb-owner/00-smoke.md.tpl` con frontmatter (persona, journey, version, idioma) + 2 placeholders
- Primer test stub `00-smoke.spec.ts`: navega a la URL del demo + 1 step etiquetado
- Renderer prototipo TS (`manual-renderer/render.ts`, ~150 líneas): lee Allure JSON + template → produce `.md` con 1 imagen embebida
- Validar: `npx playwright test --project=manuales` produce trace + ejecutar renderer produce `docs/manuales/auto/v2.5.4/es/smb-owner/00-smoke.md` legible

**Criterio de salida:** 1 manual trivial auto-generado, legible, con 1 screenshot real.

### Fase 1 — SMB Owner Día 1 walking skeleton (~1 semana)

Objetivo: primer manual real, end-to-end, calidad pedagógica comparable a `docs/manuales/smb/01-instalacion-docker.md` + `04-canal-webchat.md`.

- Journey: instalar Verbara via `docker compose -f docker/docker-compose.reference-smb.yml up` → setup wizard (`POST /api/v1/setup`) → admin login → menú Canales → configurar WebChat → copiar embed → simular widget en página HTML mínima → recibir primer mensaje
- ~15 steps etiquetados (`test.step('...', ...)` con title que matchea placeholder en template)
- Template `01-day1-setup-and-webchat.md.tpl` escrito por humano con: secciones (Pre-requisitos, Instalación, Setup inicial, Configurar WebChat, Verificar primer mensaje, Troubleshooting), warnings, OJOs, links cruzados
- Test usa fixtures de `helpers/credentials.ts` + extiende `ApiHelper` si necesario
- CI smoke: `npx playwright test --project=manuales tests/manuales/personas/smb-owner/01-*.spec.ts` corre limpio
- Comparación humana: lado a lado vs `docs/manuales/smb/01-instalacion-docker.md` — review por maintainer

**Criterio de salida:** Manual SMB Owner Día 1 auto-generado, aprobado en review humano como "calidad pedagógica equivalente o superior" al escrito a mano.

### Fase 2 — Multi-render (~1 semana)

Objetivo: el manual existe en 3 formatos consumibles.

- Setup MkDocs Material en `docs-site/`: nav por persona/journey, search, theme corporate
- Pipeline: `npm run docs:build` (en Platform.Web) corre tests manuales + renderer + `mkdocs build` → genera site estático en `docs-site/site/`
- Extender [`src/Verbara.Platform.Renderer/`](../../../src/Verbara.Platform.Renderer/) con endpoint `POST /render/manual` que recibe path a un `.md` + array de screenshots y produce PDF via QuestPDF (consistencia stack, AOT-compatible, evita Pandoc)
- Sample PDF descargable desde el site
- README de la infra: cómo correr local + cómo regenerar tras un cambio

**Criterio de salida:** `mkdocs serve` muestra el manual del Día 1 + descarga PDF visualmente equivalente.

### Fase 3 — Expandir SMB Owner persona (~1-2 semanas)

Objetivo: paridad con los 12 manuales SMB actuales.

- Journeys totales SMB Owner: 5
  1. Día 1 — Instalar + WebChat (Fase 1 ya)
  2. Día 2 — Email channel (MailKit SMTP + MS Graph OAuth PKCE)
  3. Día 3 — Voice/SIP channel (Asterisk PBX + WebRTC extension provisioning)
  4. Día 7 — Primer reporte + facturación básica
  5. Día 30 — Troubleshooting + diagnóstico
- Mapeo 1:1 con `docs/manuales/smb/` 01/02/03/04/05/06/07/08/99 + checklist
- Migración: cuando los 5 journeys SMB Owner están en `auto/`, deprecar `docs/manuales/smb/` con redirect a `docs/manuales/auto/v2.5.4/es/smb-owner/`

**Criterio de salida:** 5 manuales SMB Owner auto-generados, sitio HTML indexa todos, README del proyecto apunta al sitio en vez de `docs/manuales/smb/`.

### Fase 4 — Otras personas (escalonado, ~4-6 semanas)

Objetivo: cobertura completa de los 8 roles + tenant hierarchy.

- Tenant Admin (~8 journeys): provisioning de queues, agentes, skills, flows, bots, knowledge base, configuración de canal adicional, audit + GDPR
- Supervisor (~6): wallboard, whisper/listen/barge, takeover, transfers, agente intervals, surveys monitoring
- Agent (~4): aceptar conversación, transferir (consulted/blind), usar AgentAssist, set state (available/paused)
- QA Analyst (~3): scorecards, evaluations, recording playback
- Platform Admin (~5): crear partner tenant, impersonation, cluster management, license, multi-tenant settings
- Partner Admin (~4): crear customer tenant, billing, revenue, partner settings
- Total: ~30 journeys, 30 manuales

**Criterio de salida:** Sitio de docs tiene navegación completa por persona; cada persona tiene al menos 3 journeys cubiertos.

### Fase 5 — K8s parity (deferred, ~1-2 semanas)

Objetivo: mismos tests corriendo contra Talos lab, manuales K8s sólo donde difieren del Docker.

- Variable `MANUAL_TARGET=docker|k8s` en config Playwright; selecciona base URL + auth pattern
- Diff detector (script): compara renders Docker vs K8s, alerta diferencias estructurales (steps añadidos/quitados/diferente comportamiento)
- Manuales K8s heredan Docker como base + secciones delta donde aplique
- Output: `docs/manuales/auto/v2.5.4/es/smb-owner/01-day1-setup-and-webchat.k8s.md` con notas "en K8s, en lugar de docker compose up, usar `helm upgrade ...`"

**Criterio de salida:** Lab Talos arriba → mismos 5 manuales SMB Owner se regeneran contra K8s; delta visible en sitio. Gated en disponibilidad de lab + demanda de cliente K8s.

### Fase 6 — i18n + multi-version (deferred, gated)

Objetivo: ES canonical + EN + PT-BR; versionado por release.

- Diccionarios de step titles por idioma en `tests/manuales/i18n/{es,en,pt-br}.json`
- Test corre 1 vez por idioma (mismo flujo, mismos screenshots, narrativa traducida)
- Estructura URL: `docs/manuales/auto/{version}/{locale}/{persona}/{journey}.md`
- Traducción auto via LLM (Claude) + revisión humana antes de release
- Gated en: primer cliente que pida non-ES o primer release post-v2.5.4 que justifique versionado dual

**Criterio de salida:** 1 journey SMB Owner Día 1 disponible en ES + EN + PT-BR; site selector de idioma funcional.

## Critical files

**A crear (Fase 0-1):**
- [tests/manuales/](../../../../Verbara.Platform.Web/tests/manuales/) — nuevo árbol
- `tests/manuales/playwright.docs.config.ts`
- `tests/manuales/manual-renderer/render.ts` + `allure-adapter.ts` + `template-engine.ts`
- `tests/manuales/personas/smb-owner/01-day1-setup-and-webchat.md.tpl`
- `tests/manuales/personas/smb-owner/01-day1-setup-and-webchat.spec.ts`

**A modificar (Fase 2):**
- [src/Verbara.Platform.Renderer/](../../../src/Verbara.Platform.Renderer/) — agregar endpoint `/render/manual` (consume MD + screenshots, produce PDF via QuestPDF). Patrón existente: ver endpoints de reports analíticos actuales.
- `package.json` del frontend — agregar `allure-playwright` devDep + scripts `docs:test`, `docs:render`, `docs:build`

**A reusar (referencia, NO modificar inicialmente):**
- [tests/e2e/helpers/credentials.ts](../../../../Verbara.Platform.Web/tests/e2e/helpers/credentials.ts) — fixtures de auth
- [tests/e2e/fixtures/api.fixture.ts](../../../../Verbara.Platform.Web/tests/e2e/fixtures/api.fixture.ts) — ApiHelper para setup vía API
- [tests/e2e/tests/reference-deployment.spec.ts](../../../../Verbara.Platform.Web/tests/e2e/tests/reference-deployment.spec.ts) — referencia del @reference-deployment style
- [docs/manuales/smb/01-instalacion-docker.md](../../manuales/smb/01-instalacion-docker.md) + [04-canal-webchat.md](../../manuales/smb/04-canal-webchat.md) — target de calidad pedagógica para Fase 1

**Output (creado automáticamente, no commit hasta Fase 3 — antes solo local):**
- `docs/manuales/auto/v2.5.4/es/smb-owner/*.md` + `*/step-*.png`

## Verification

**Fase 0 (smoke):**
```bash
cd /media/Data/Source/Verbara/Verbara.Platform.Web
npx playwright test --project=manuales tests/manuales/personas/smb-owner/00-smoke.spec.ts
node tests/manuales/manual-renderer/render.ts --input allure-results/ --template tests/manuales/personas/smb-owner/00-smoke.md.tpl --output docs/manuales/auto/v2.5.4/es/smb-owner/00-smoke.md
cat docs/manuales/auto/v2.5.4/es/smb-owner/00-smoke.md
```
Esperado: archivo `.md` con frontmatter + 2 secciones + 1 imagen embebida que se renderiza en VSCode preview.

**Fase 1 (primer manual real):**
```bash
docker compose -f docker/docker-compose.reference-smb.yml up -d
npx playwright test --project=manuales tests/manuales/personas/smb-owner/01-day1-setup-and-webchat.spec.ts
node tests/manuales/manual-renderer/render.ts --persona smb-owner --journey 01
```
Esperado: `01-day1-setup-and-webchat.md` con ~15 secciones + ~15 screenshots, narrativa coherente, review humano lo aprueba.

**Fase 2 (multi-render):**
```bash
npm run docs:build
mkdocs serve  # http://localhost:8000 muestra manual con nav
curl -X POST http://localhost:5010/render/manual -d @manual-request.json -o manual.pdf
```

**Fase 3+ (criterios cualitativos):**
- Review humano de paridad pedagógica vs manuales SMB actuales
- Click-through del sitio: cualquier journey deja claro el siguiente paso
- Tiempo de ejecución del suite `manuales` < 15 min para todos los SMB Owner journeys

## Out of scope / deferrals explícitos

- **Video segmentado por step (ffmpeg cuts)** — bajo ROI vs costo de mantenimiento; el video raw de Playwright ya queda disponible para customer demos sin segmentar. Re-evaluar si llega pedido específico de cliente.
- **Scribe.how / Tango.us / Supademo / cualquier SaaS proprietary** — descartado por criterio "producto final sin atajos". Si fuera viable, sería 1 día de trabajo pero genera dependencia externa + costo recurrente.
- **Generación de docs en runtime (in-app help)** — Fase futura post-Fase 6, requiere infra distinta.
- **Cobertura E2E de los 70 endpoint groups del API** — los manuales cubren los flujos *visibles* en UI; cobertura API completa es un esfuerzo separado (E2E.Harness backend + futuro work).
- **Localización de los specs en sí mismos** — los `test.step('title', ...)` quedan en español (canonical); la traducción ocurre en el render-time vía diccionarios (Fase 6).
- **CI gating del release.yml en base a manuales pass/fail** — Fase 4+ después de validar estabilidad; inicialmente los manuales son advisory, no bloquean release.

## Riesgos conocidos + mitigaciones

| Riesgo | Mitigación |
|---|---|
| UI cambia y rompe N manuales en cascada | Capturas auto-regeneran; narrativa intacta. Test falla, alertamos, regeneramos screenshots. Pipeline barato. |
| Video on hace el suite lento (~30-40% overhead) | Project `manuales` separado; CI corre solo en release branches o on-demand, no en cada PR. Project `verify` (74 specs existentes) sigue rápido. |
| Allure trace format cambia entre versiones de Playwright | Adapter aislado en `allure-adapter.ts`; si formato cambia, sólo ese archivo se toca. |
| Mantener 5 + 8 + 6 + 4 + 3 + 5 + 4 = 35 journeys × N releases es trabajo recurrente | Fase 3 valida si el pipeline escala antes de comprometer todas las personas. Si la regeneración por release toma > 1h, replanteamos arquitectura. |
| Drift entre `docs/manuales/smb/` (escrito a mano, vivo hoy) y `docs/manuales/auto/` (en construcción) | Mantener ambos durante Fases 0-2; deprecar smb/ en Fase 3 cuando paridad esté validada. |
