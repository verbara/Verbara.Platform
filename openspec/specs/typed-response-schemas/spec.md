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

