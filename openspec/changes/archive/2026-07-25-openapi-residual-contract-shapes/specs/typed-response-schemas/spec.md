## ADDED Requirements

### Requirement: The emitted ComplianceRuleSummaryDto schema declares severity as a closed enum

The emitted OpenAPI document SHALL declare the `severity` property of the `ComplianceRuleSummaryDto`
schema as a closed `string` enum with exactly the values `Info`, `Warning`, and `Critical`, never an
open `string`. A sibling `IOpenApiSchemaTransformer` registered on the same `AddOpenApi()` seam
(`Program.cs`) as `NumericSchemaTruthTransformer` MUST narrow the emitted `severity` schema
**document-only** — it rewrites the built `OpenApiSchema` model and MUST NOT change the DTO's C# type,
its `[JsonSerializable]` registration in `ApiJsonContext`, runtime serialization, or deserialization.
The `ComplianceRuleSummaryDto` member `Severity` SHALL remain a `string`-typed sealed-record property
(the server keeps writing plain strings; only the document's declared type narrows). The three enum
values MUST match `fixtures/compliance-rule-summary.v1.json` verbatim — `severity` ∈ {`Info`,
`Warning`, `Critical`} — and the DTO's other emitted fields MUST remain `ruleId`, `ruleName`,
`occurrences`, `sessionsAffected`, `firstSeen`, `lastSeen` (verbatim, unchanged).

#### Scenario: The compliance-rule-summary severity schema is a closed enum

- **GIVEN** the emitted document's `ComplianceRuleSummaryDto` schema
- **WHEN** the OpenAPI document is captured in CI (runtime capture, ADR-0035/ADR-0036)
- **THEN** the `severity` property is `type: string` with `enum: [Info, Warning, Critical]` — the three
  values verbatim from `fixtures/compliance-rule-summary.v1.json`, no open string
- **AND** the sibling fields `ruleId`, `ruleName`, `occurrences`, `sessionsAffected`, `firstSeen`,
  `lastSeen` are present with their existing types, unchanged

#### Scenario: The severity narrowing is document-only

- **GIVEN** a compliance summary response produced by the server before and after the transformer ships
- **WHEN** the same request is issued
- **THEN** the HTTP status code and the `severity` value written on the wire are identical (a plain
  string) — only the emitted OpenAPI document's `severity` schema differs; no `ApiJsonContext` entry,
  no `JsonNumberHandling`, and no handler code changes

### Requirement: The emitted TopicTrendsResponse schema uses trends, not topics

The emitted OpenAPI document SHALL declare the `TopicTrendsResponse` schema with a `trends` array
property (each element `{topic, occurrences, avgConfidence}`) plus a `totalAnalyzed` integer, and MUST
NOT declare a `topics` property or `from`/`to` properties. This is asserted as a regression guard on
the already-correct producer shape (`record TopicTrendsResponse(TopicTrendDto[] Trends, int
TotalAnalyzed)`); no producer code change is made by this capability. The field names MUST match
`fixtures/topic-trends-response.v1.json` verbatim — the array key `trends`, each element's `topic`,
`occurrences`, `avgConfidence`, and the root `totalAnalyzed`.

#### Scenario: The captured TopicTrendsResponse schema exposes trends verbatim

- **GIVEN** the emitted document's `TopicTrendsResponse` schema
- **WHEN** the OpenAPI document is captured in CI
- **THEN** it contains a `trends` array whose element schema has `topic`, `occurrences`, and
  `avgConfidence`, plus a root `totalAnalyzed` — each name verbatim from
  `fixtures/topic-trends-response.v1.json`
- **AND** it contains no `topics` property and no `from`/`to` properties

## Architectural Risk

**Level:** LOW

**Affected:** `Verbara.Platform` (the emitted OpenAPI document for the `analytics` endpoint group,
`CallAnalyticsEndpoints.cs`; the shared `AddSchemaTransformer` seam at `Program.cs`) and, downstream,
`Verbara.Platform.Web` (regenerates `openapi.d.ts` and narrows its `ComplianceRuleSummaryDto` /
`TopicTrendsResponse` hand-written shadows to the corrected shapes — the child change). No effect on
`Verbara.Sdk` or `Verbara.Sdk.Pro` (neither consumes the emitted document).

**Mitigation:** the severity narrowing is document-only and AOT-safe (no reflection over user types,
no DTO/`ApiJsonContext` change), matching the proven `NumericSchemaTruthTransformer` pattern; the new
transformer targets only the `severity` string on one named schema, orthogonal to the numeric
transformer. The `TopicTrendsResponse` requirement is an assert-only regression guard on an
already-correct shape (no producer edit). All three shapes are verified verbatim against the golden
fixtures in the CI-runtime-captured document, and Web narrows its shadow only after Platform re-emits
the corrected contract (hard buildOrder barrier, `impact.yaml`).
