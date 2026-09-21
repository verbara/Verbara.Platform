# Tasks: openapi-residual-contract-shapes (Platform host / producer)

> Host (producer) tasks for the cross-repo `openapi-residual-contract-shapes` change. Only ONE shape
> requires producer code — `ComplianceRuleSummaryDto.severity` — via a sibling `IOpenApiSchemaTransformer`
> on the same seam as `NumericSchemaTruthTransformer` (design.md D1). `TopicTrendsResponse` and
> `PagedResult<T>` are assert/verify-only against already-correct emitted shapes (D2, D3). The
> corrected captured document is the Stage-2 (Web) handoff artifact. The Web shadow retirement is the
> child change (`web/openapi-residual-contract-shapes`), staged after this host per `impact.yaml`.

## 1. The severity schema transformer (Phase A — foundation)

- [x] 1.1 Add `ComplianceSeverityEnumTransformer` (`IOpenApiSchemaTransformer`) in
  `src/Verbara.Platform.Api/OpenApi/`: for the emitted `ComplianceRuleSummaryDto` schema, narrow the
  `severity` string property to a closed enum `[Info, Warning, Critical]` (values sourced from
  `ComplianceSeverityBreakdownDto`, `CallAnalyticsEndpoints.cs:382-385`). Document-only, AOT-safe (no
  reflection over user types); the DTO member `Severity` stays `string` and stays in
  `Serialization/ApiJsonContext.cs:395-396`. Targets only the `severity` property on that one named
  schema — orthogonal to `NumericSchemaTruthTransformer`.
- [x] 1.2 Register it at `Program.cs:1633` on the existing seam —
  `AddOpenApi(o => o.AddSchemaTransformer<NumericSchemaTruthTransformer>().AddSchemaTransformer<ComplianceSeverityEnumTransformer>())`
  (both transformers on the one `AddOpenApi` call).

## 2. Tests (Phase B — critical component)

- [x] 2.1 Unit-test the transformer (`ComplianceSeverityEnumTransformerTests`): the `ComplianceRuleSummaryDto`
  `severity` schema is narrowed to `type: string` with `enum: [Info, Warning, Critical]`; other
  properties and other schemas untouched; idempotent; no numeric schema affected.
- [x] 2.2 Integration-test the emitted document (`ResidualContractShapesCaptureTests`): boot the host
  in-memory (`Platform:OpenApi:Enabled=true`), fetch `/openapi/v1.json`, and assert (a)
  `ComplianceRuleSummaryDto.severity` is the closed enum + sibling fields `ruleId`, `ruleName`,
  `occurrences`, `sessionsAffected`, `firstSeen`, `lastSeen`; (b) `TopicTrendsResponse` emits
  `trends`/`totalAnalyzed` with no `topics`/`from`/`to`; (c) the `PagedResult<T>` envelope declares
  `items`, `totalCount`, `page`, `pageSize`, `totalPages`, `hasNextPage`, `hasPreviousPage`.

## 3. Fixture verification / handoff (Phase C — integration)

- [x] 3.1 Extend the existing verbatim-field assertion (`scripts/verify-openapi-fixture.py`-style) to
  verify the three residual-shape fixtures against the CI-runtime-captured document:
  `fixtures/compliance-rule-summary.v1.json`, `fixtures/topic-trends-response.v1.json`,
  `fixtures/paged-result-envelope.v1.json` — field names verbatim per the `openapi-export` delta.
- [x] 3.2 Capture the corrected document (Stage-2 Web handoff); confirm `ComplianceRuleSummaryDto.severity`
  emits the closed enum and `TopicTrendsResponse`/`PagedResult<T>` match their fixtures. Record the
  `PagedResultOf<T>` monomorphization as by-design (no producer change). Capture mechanism: the
  `ResidualContractShapesCaptureTests` capture test writes the document when `CAPTURE_OPENAPI_PATH` is set,
  mirroring the CI-runtime export lane (no headless `dotnet getdocument` — the host's ~28 eager-Postgres
  IHostedServices make design-time export infeasible, per `Program.cs:1620-1627`).

## 4. Records

- 4.1 (NO ES TAREA — opcional, omitida deliberadamente) Author ADR-0037 recording the "severity as a
  document-only enum" ruling and the "PagedResult monomorphization is by-design" ruling. Nunca se
  autoró y no se debe: ambos fallos quedan registrados en `design.md` (D1 §57-85, D3 §96-100), en la
  tarea 3.2 y en `CHANGELOG.md:515-521` ("Document-only, no runtime change", #191); el change lleva
  `decision_ref: Platform/ADR-0036` y su propio Open Questions ya lo marca "Deferred". Verificado
  2026-09-20: ADR-0036 no menciona `severity` ni `PagedResult` (grep: cero hits), y el hueco 0037 lo
  ocupa `docs/decisions/0037-canonical-rbac-permission-vocabulary.md` (2026-08-12) — si alguien
  elevara estos fallos a ADR necesitaría un número NUEVO, jamás el 0037. No reabrir.
  <!-- deferred — ADR-0036 covers this class (operator proceeded to apply without requesting a new ADR). -->
- [x] 4.2 Add the `[Unreleased]` CHANGELOG entry.

## 5. Verification gate

- [x] 5.1 `dotnet build Verbara.Platform.slnx -c Release` and `dotnet test` green — zero warnings
  (`TreatWarningsAsErrors=true`, `WarningLevel=9999`), no new AOT (`IL2026`/`IL3050`/`IL207x`)
  diagnostics; `openspec validate --change openapi-residual-contract-shapes --strict` green; CI green.
  Evidencia (verificada 2026-09-20): PR #191 `fix(openapi): declare ComplianceRuleSummaryDto.severity as
  closed enum`, MERGED 2026-07-25T19:21:08Z con todo el rollup en SUCCESS — `Build + Unit Tests
  (Release)` (build+tests, cero warnings), `AOT Publish (Api)` (sin nuevos IL2026/IL3050/IL207x),
  `OpenSpec Validate` (`--strict`), `Invariant Gates`, `Coverage Ratchet`, `Live-DB Tests (Postgres)`,
  `CodeQL` (`gh pr view 191 --json statusCheckRollup`). Archivado por PR #192 (9db56949).

## 6. Cross-repo handoff (Web child change — NOT this host's edit)

- [x] 6.1 After this host lands and CI re-captures the corrected document, the Web child change
  (`web/openapi-residual-contract-shapes`, buildOrder 2 per `impact.yaml`) regenerates
  `src/core/api/generated/openapi.d.ts` (`npm run generate:api-types`), retires the `TopicTrendsResponse`
  and `ComplianceRuleSummaryDto` hand-written shadows in `src/core/api/hooks/use-analytics.ts`, and
  repoints the `speech-analytics-page.tsx` consumers (`topics`→`trends`; severity display/filter/sort).
  Web verification gate: `npm run build`, `npx vitest run`, `npx eslint .`, i18n parity green. Driven by
  `/xr:apply` (staged after this host — hard contract barrier). `PagedResult`: NO Web action here.
  Evidencia (verificada 2026-09-20 en el árbol de Verbara.Platform.Web, no sólo en su registro):
  change hijo archivado en `openspec/changes/archive/2026-07-25-openapi-residual-contract-shapes/`;
  PR Web #226 MERGED 2026-07-25T18:59:09Z con `build`/`test`/`lint`/`i18n` en SUCCESS (archivado por
  #227, 114dd07a). Código: `src/core/api/generated/openapi.d.ts:16763` emite
  `severity: 'Info' | 'Warning' | 'Critical'`; `use-analytics.ts:335,346,351` ya son alias de
  `components['schemas'][…]` (shadows retirados); `speech-analytics-page.tsx:71` lee `data?.trends`,
  `:290` filtra sobre la unión generada. `PagedResult`: sin acción Web, según lo dictaminado.
  Anomalía de orden registrada: el PR Web entró 22 min ANTES del host #191 (19:21) — regeneró contra el
  documento de la rama del host, no tras su merge; el estado final es el correcto.
  Consistencia con el registro Web: su propia caja 5.4 (spec Playwright de speech-analytics) volvió NO
  HECHA y está cosechada en el change abierto Web `analytics-contract-residue` (tarea 2.5) — queda fuera
  de la puerta que esta caja enumera, que no pide Playwright.
