# Tasks: openapi-residual-contract-shapes (Platform host / producer)

> Host (producer) tasks for the cross-repo `openapi-residual-contract-shapes` change. Only ONE shape
> requires producer code — `ComplianceRuleSummaryDto.severity` — via a sibling `IOpenApiSchemaTransformer`
> on the same seam as `NumericSchemaTruthTransformer` (design.md D1). `TopicTrendsResponse` and
> `PagedResult<T>` are assert/verify-only against already-correct emitted shapes (D2, D3). The
> corrected captured document is the Stage-2 (Web) handoff artifact. The Web shadow retirement is the
> child change (`web/openapi-residual-contract-shapes`), staged after this host per `impact.yaml`.

## 1. The severity schema transformer (Phase A — foundation)

- [ ] 1.1 Add `ComplianceSeverityEnumTransformer` (`IOpenApiSchemaTransformer`) in
  `src/Verbara.Platform.Api/OpenApi/`: for the emitted `ComplianceRuleSummaryDto` schema, narrow the
  `severity` string property to a closed enum `[Info, Warning, Critical]` (values sourced from
  `ComplianceSeverityBreakdownDto`, `CallAnalyticsEndpoints.cs:382-385`). Document-only, AOT-safe (no
  reflection over user types); the DTO member `Severity` stays `string` and stays in
  `Serialization/ApiJsonContext.cs:395-396`. Targets only the `severity` property on that one named
  schema — orthogonal to `NumericSchemaTruthTransformer`.
- [ ] 1.2 Register it at `Program.cs:1633` on the existing seam —
  `AddOpenApi(o => o.AddSchemaTransformer<NumericSchemaTruthTransformer>().AddSchemaTransformer<ComplianceSeverityEnumTransformer>())`
  (both transformers on the one `AddOpenApi` call).

## 2. Tests (Phase B — critical component)

- [ ] 2.1 Unit-test the transformer (`ComplianceSeverityEnumTransformerTests`): the `ComplianceRuleSummaryDto`
  `severity` schema is narrowed to `type: string` with `enum: [Info, Warning, Critical]`; other
  properties and other schemas untouched; idempotent; no numeric schema affected.
- [ ] 2.2 Integration-test the emitted document (`ResidualContractShapesCaptureTests`): boot the host
  in-memory (`Platform:OpenApi:Enabled=true`), fetch `/openapi/v1.json`, and assert (a)
  `ComplianceRuleSummaryDto.severity` is the closed enum + sibling fields `ruleId`, `ruleName`,
  `occurrences`, `sessionsAffected`, `firstSeen`, `lastSeen`; (b) `TopicTrendsResponse` emits
  `trends`/`totalAnalyzed` with no `topics`/`from`/`to`; (c) the `PagedResult<T>` envelope declares
  `items`, `totalCount`, `page`, `pageSize`, `totalPages`, `hasNextPage`, `hasPreviousPage`.

## 3. Fixture verification / handoff (Phase C — integration)

- [ ] 3.1 Extend the existing verbatim-field assertion (`scripts/verify-openapi-fixture.py`-style) to
  verify the three residual-shape fixtures against the CI-runtime-captured document:
  `fixtures/compliance-rule-summary.v1.json`, `fixtures/topic-trends-response.v1.json`,
  `fixtures/paged-result-envelope.v1.json` — field names verbatim per the `openapi-export` delta.
- [ ] 3.2 Capture the corrected document (Stage-2 Web handoff); confirm `ComplianceRuleSummaryDto.severity`
  emits the closed enum and `TopicTrendsResponse`/`PagedResult<T>` match their fixtures. Record the
  `PagedResultOf<T>` monomorphization as by-design (no producer change).

## 4. Records

- [ ] 4.1 (Optional) Author ADR-0037 (`docs/decisions/0037-*.md`) recording the "severity as a
  document-only enum" ruling and the "PagedResult monomorphization is by-design" ruling — amends/extends
  Platform/ADR-0036. Skip only if the operator confirms ADR-0036 suffices.
- [ ] 4.2 Add the `[Unreleased]` CHANGELOG entry.

## 5. Verification gate

- [ ] 5.1 `dotnet build Verbara.Platform.slnx -c Release` and `dotnet test` green — zero warnings
  (`TreatWarningsAsErrors=true`, `WarningLevel=9999`), no new AOT (`IL2026`/`IL3050`/`IL207x`)
  diagnostics; `openspec validate --change openapi-residual-contract-shapes --strict` green; CI green.

## 6. Cross-repo handoff (Web child change — NOT this host's edit)

- [ ] 6.1 After this host lands and CI re-captures the corrected document, the Web child change
  (`web/openapi-residual-contract-shapes`, buildOrder 2 per `impact.yaml`) regenerates
  `src/core/api/generated/openapi.d.ts` (`npm run generate:api-types`), retires the `TopicTrendsResponse`
  and `ComplianceRuleSummaryDto` hand-written shadows in `src/core/api/hooks/use-analytics.ts`, and
  repoints the `speech-analytics-page.tsx` consumers (`topics`→`trends`; severity display/filter/sort).
  Web verification gate: `npm run build`, `npx vitest run`, `npx eslint .`, i18n parity green. Driven by
  `/xr:apply` (staged after this host — hard contract barrier). `PagedResult`: NO Web action here.
