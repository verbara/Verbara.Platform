# typed-response-schemas Specification

## Purpose
TBD - created by archiving change openapi-response-schemas. Update Purpose after archive.
## Requirements
### Requirement: Consumer-facing endpoint groups return typed results that surface named response schemas

Handlers in the consumer-facing endpoint groups (`admin-remainder`, `agent`, `analytics`,
`operations`) SHALL return the typed `Task<Results<Ok<TDto>, ...>>` shape and produce their success
body via `TypedResults.*` (the proven `CsatResponseEndpoints.cs` pattern), rather than an untyped
`Task<IResult>` returned via `Results.Ok(...)` / `Results.Json(...)`, so each success DTO surfaces
as a named `components/schemas` entry in the emitted `/openapi/v1.json` document. Converted handlers
MUST NOT introduce reflection — `TypedResults` is AOT-safe and every response DTO already lives in a
`[JsonSerializable]` source-gen context (`ApiJsonContext`); no DTO is removed from that context.

#### Scenario: A converted handler emits a named response schema

- **GIVEN** an endpoint in a converted group that previously returned `Task<IResult>` via
  `Results.Ok(dto)`
- **WHEN** it is converted to `Task<Results<Ok<TDto>, ...>>` returning `TypedResults.Ok(dto)` and the
  OpenAPI document is captured
- **THEN** `components/schemas` contains a named entry for `TDto`'s emitted schema whose field names
  match the DTO's serialized property names verbatim

### Requirement: The conversion is phased by consumer need

The conversion SHALL proceed group-by-group in the order the Web consumer requires
(`admin-remainder`, then `agent`, `analytics`, `operations`), NOT as a single big-bang rewrite of all
~415 handlers. Each phase MAY ship independently once its group's schemas are emitted and its manifest
group is complete; a phase MUST NOT be considered done while any handler in its group still returns an
untyped `Task<IResult>` for a success body the manifest declares.

#### Scenario: A group phase ships without requiring the other groups

- **GIVEN** the `admin-remainder` group is fully converted and its manifest group is complete
- **WHEN** the change's first phase is validated
- **THEN** the `admin-remainder` schemas are present in the captured document and verified against the
  manifest, independent of whether `agent`, `analytics`, or `operations` are converted yet

### Requirement: Response bodies are byte-identical at runtime

The conversion SHALL NOT change any endpoint's request or response contract, status codes, gating, or
the JSON serialized on the wire. `TypedResults.Ok(dto)` MUST serialize the same `dto` value as the
prior `Results.Ok(dto)` — this change adds OpenAPI schema metadata only, never new runtime behavior.

#### Scenario: Wire body is unchanged after conversion

- **GIVEN** an endpoint whose handler is converted from `Results.Ok(dto)` to `TypedResults.Ok(dto)`
- **WHEN** the same request is issued before and after the conversion
- **THEN** the HTTP status code and the response body bytes are identical; only the OpenAPI document's
  `components/schemas` differs

### Requirement: The build stays warning-clean and AOT-compatible

The converted handlers SHALL compile under `TreatWarningsAsErrors=true` / `WarningLevel=9999` with
zero warnings, and `Verbara.Platform.Api` MUST remain Native-AOT-compatible (no `IL2026`/`IL3050`/
`IL207x` diagnostics introduced). No reflection, no `Activator.CreateInstance`, no anonymous
response objects.

#### Scenario: Converted build is green and AOT-clean

- **GIVEN** a phase's handlers converted to the typed-result pattern
- **WHEN** `dotnet build Verbara.Platform.slnx -c Release` and the AOT publish run
- **THEN** the build succeeds with zero warnings and no new AOT trim/analysis diagnostics

### Requirement: Numeric schema fields are declared with a single JSON type

The emitted OpenAPI document SHALL declare every numeric body/response schema field with a **single**
JSON type (`integer` or `number`) plus its `format`, never the .NET 10 `JsonSchemaExporter`
`["integer","string"]` / `["number","string"]` union (dotnet/aspnetcore #64145). An
`IOpenApiSchemaTransformer` registered on `AddOpenApi()` (`Program.cs`) MUST strip the spurious
`string` arm **document-only** — it rewrites the built `OpenApiSchema` model and MUST NOT change
runtime deserialization (`JsonNumberHandling.AllowReadingFromString` is retained; this is NOT
`NumberHandling.Strict`). Any field that genuinely exceeds 2^53 MUST instead be modeled as an explicit
`string` DTO property (so the schema declares `type: string` deliberately), never via the blanket
numeric union.

#### Scenario: A CSAT aggregate numeric field declares a single type

- **GIVEN** the emitted document's `CsatAggregateDto` schema
- **WHEN** the OpenAPI document is captured in CI (runtime capture, ADR-0035)
- **THEN** `totalResponses` is `type: integer` (`format: int32`) and `averageRating` is `type: number`
  (`format: double`) — each a single JSON type with no `string` arm, matching
  `fixtures/openapi-numeric-schema.v1.json`

#### Scenario: nullable numerics keep their null arm but lose the string arm

- **GIVEN** the emitted document's `DashboardKpisDto` and `QueueMetricsDto` schemas
- **WHEN** the document is captured
- **THEN** `DashboardKpisDto.avgWaitMs` is `type: number` (`format: double`) — single-typed, no `string` arm
- **AND** `QueueMetricsDto.waiting` is a nullable integer — rendered in OpenAPI 3.1 as
  `type: ["null","integer"]` (`format: int32`), i.e. `integer | null`, never `| string` — each field
  matching `fixtures/openapi-numeric-schema.v1.json` verbatim (the transformer preserves the `null`
  arm and strips only `string`)

#### Scenario: Runtime request leniency is unchanged

- **GIVEN** a caller that POSTs a numeric field as a quoted string (e.g. `"42"`)
- **WHEN** the request is deserialized after the transformer ships
- **THEN** it still succeeds (the transformer is document-only; `AllowReadingFromString` is retained) —
  the document states the canonical numeric form while the runtime stays lenient

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

