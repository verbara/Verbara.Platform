## ADDED Requirements

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

#### Scenario: int64 and nullable numerics are single-typed too

- **GIVEN** the emitted document's `DashboardKpisDto` and `QueueMetricsDto` schemas
- **WHEN** the document is captured
- **THEN** `DashboardKpisDto.avgWaitMs` is `type: integer` (`format: int64`) and `QueueMetricsDto.waiting`
  is a nullable `integer` — single-typed with no `string` arm, matching
  `fixtures/openapi-numeric-schema.v1.json`

#### Scenario: Runtime request leniency is unchanged

- **GIVEN** a caller that POSTs a numeric field as a quoted string (e.g. `"42"`)
- **WHEN** the request is deserialized after the transformer ships
- **THEN** it still succeeds (the transformer is document-only; `AllowReadingFromString` is retained) —
  the document states the canonical numeric form while the runtime stays lenient
