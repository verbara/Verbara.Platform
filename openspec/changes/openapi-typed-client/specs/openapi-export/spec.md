## ADDED Requirements

### Requirement: CI exports the OpenAPI document as a build artifact

The build SHALL produce the Api host's OpenAPI 3.0 document as a build-time output (via
`Microsoft.Extensions.ApiDescription.Server` document generation over the existing
`AddOpenApi()` registration) without requiring a running host, a live database/Redis connection,
or the `Platform:OpenApi:Enabled` runtime flag.

#### Scenario: Release-configuration build produces the document

- **GIVEN** a `dotnet build Verbara.Platform.slnx -c Release` invocation in CI
- **WHEN** the build completes
- **THEN** the generated OpenAPI document for `Verbara.Platform.Api` exists in the build output,
  independent of `Platform:OpenApi:Enabled` or the ASPNETCORE_ENVIRONMENT value

### Requirement: CI publishes the exported document as a versioned, downloadable artifact

The `build-and-test` workflow job SHALL upload the exported OpenAPI document as a CI artifact
scoped to the triggering commit/run, so downstream consumers (Web's typed-client codegen) can
download a specific Platform version's document without checking out or building Platform.

#### Scenario: Artifact is retrievable after a CI run

- **GIVEN** a completed `build-and-test` job on a commit
- **WHEN** a consumer requests that run's artifacts
- **THEN** the OpenAPI document produced from that commit is downloadable as a named CI artifact

### Requirement: The golden fixture is verified against the real emitted document

`fixtures/openapi-document.v1.sample.json` SHALL be checked, as part of this change's validation,
against the corresponding schema fragment of the real build-time-exported OpenAPI document, so the
fixture cannot silently drift from the real contract the way the hand-written Web `CsatResponseDto`
consumer type did (Web PR#159, v3.13.1-web).

The exported document SHALL match the golden wire fixture verbatim (the
verbatim-fixture-citation rule, `/xr:propagate`): the envelope keys `openapi`, `info`, `paths`,
and `components.schemas`; the sample path `/api/v1/analytics/csat/queues/{queueId}`; and the
`CsatResponseDto` schema with these 6 fields exactly:

- `queueName`
- `channel`
- `totalResponses`
- `averageRating`
- `rangeStart`
- `rangeEnd`

#### Scenario: Fixture matches the real document's schema fragment

- **GIVEN** the build-time-exported OpenAPI document and the existing
  `fixtures/openapi-document.v1.sample.json`
- **WHEN** the fixture's `CsatResponseDto` schema fragment is compared against the corresponding
  fragment in the real document
- **THEN** field names and types match exactly (numeric/date-time formats in the fixture remain
  illustrative per the fixture's own documented intent)

### Requirement: The runtime OpenAPI surface is unaffected

Adding the build-time export mechanism SHALL NOT change the existing runtime behavior of
`/openapi/v1.json` or `/scalar/v1`, including their `IsDevelopment() ||
Platform:OpenApi:Enabled` gating.

#### Scenario: Runtime endpoints remain gated as before

- **GIVEN** a Production deployment with `Platform:OpenApi:Enabled` unset
- **WHEN** a client requests `/openapi/v1.json` or `/scalar/v1`
- **THEN** the endpoints remain unavailable, exactly as before this change

## Architectural Risk

**Level:** LOW — additive, build-time-only tooling change with no new runtime endpoints, no data
model changes, and no effect on the AOT-published binary (`Microsoft.Extensions.ApiDescription.Server`
is build/analyzer tooling, not a shipped runtime dependency).

**Affected:** `.github/workflows/ci.yml` (new export/upload step), possibly
`src/Verbara.Platform.Api/Verbara.Platform.Api.csproj` and `Directory.Packages.props` (new
build-time `PackageReference`); `fixtures/openapi-document.v1.sample.json` (verification, not
replacement). Downstream along the chain: Platform.Web's `web/openapi-typed-client` child change
consumes the published artifact.

**Mitigation:** the new package is build/analyzer-only and does not ship in the AOT-published
output, so `IsAotCompatible` / `JsonSerializerIsReflectionEnabledByDefault=false` are unaffected;
the fixture-verification task fails loudly on drift rather than silently accepting a stale sample;
no existing runtime endpoint, gating, or DTO changes.
